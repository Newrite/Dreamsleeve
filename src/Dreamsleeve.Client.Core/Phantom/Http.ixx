module;
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>
#include <winhttp.h>

export module Dreamsleeve.Client.Phantom.Http;
import std;
import Dreamsleeve.Client.Auth;
import Dreamsleeve.Client.Phantom.Wire;
#ifdef DREAMSLEEVE_DIAGNOSTICS
import Dreamsleeve.Client.Diagnostics.PhantomTrace;
#endif

export namespace Dreamsleeve::Client::Phantom
{
  // The networking owner only starts, cancels and polls bounded jobs. WinHTTP
  // callbacks signal the job owner; they never touch Exchange or game objects.
  class Http final
  {
    struct Closer { void operator()(void* value) const { if (value) WinHttpCloseHandle(value); } };
    using Handle = std::unique_ptr<void, Closer>;
    using Clock = std::chrono::steady_clock;
    // Per-job timings, emitted at most once/sec plus slow operations and endpoints.
    struct Trace
    {
#ifdef DREAMSLEEVE_DIAGNOSTICS
      const Wire::Transfer& transfer;
      Clock::time_point start{Clock::now()}, emitted{start};
      std::array<double, 5> totals{};
      std::uint32_t progress{};
      bool active{Diagnostics::Trace::Enabled()};
      explicit Trace(const Wire::Transfer& value) : transfer(value) { Emit("start", 0); }
      void Emit(std::string_view phase, double elapsed)
      {
        if (!active) return;
        Diagnostics::Trace::Event("http", std::format(
          "\"transfer\":{},\"request\":{},\"player\":{},\"generation\":{},\"upload\":{},\"body_bytes\":{},\"progress\":{},\"phase\":\"{}\",\"operation_ms\":{},\"elapsed_ms\":{},\"send_ms\":{},\"headers_ms\":{},\"budget_ms\":{},\"body_ms\":{},\"eof_ms\":{}",
          transfer.transfer.value, transfer.request.value, transfer.player, transfer.asset.generation.value,
          transfer.upload, transfer.BodyBytes(), progress, phase, elapsed,
          std::chrono::duration<double, std::milli>(Clock::now()-start).count(),
          totals[0], totals[1], totals[2], totals[3], totals[4]));
        emitted = Clock::now();
      }
      template<class F> bool Step(unsigned stage, std::string_view phase, F&& action)
      {
        if (!active) return action();
        const auto at = Clock::now();
        const bool ok = action();
        const auto ms = std::chrono::duration<double, std::milli>(Clock::now()-at).count();
        totals[stage] += ms;
        if (!ok || stage < 2 || stage == 4 || ms >= 250 || Clock::now()-emitted >= std::chrono::seconds(1)) Emit(phase, ms);
        return ok;
      }
      void Progress(std::uint32_t value) { if (!progress && value) { progress=value; Emit("first_body",0); } else progress=value; }
#else
      explicit Trace(const Wire::Transfer&) {}
      void Emit(std::string_view, double) {}
      template<class F> bool Step(unsigned, std::string_view, F&& action) { return action(); }
      void Progress(std::uint32_t) {}
#endif
    };
    struct Budget
    {
      std::mutex mutex;
      Clock::time_point due{};
      bool Wait(std::uint32_t bytes, std::uint32_t rate, std::stop_token stop)
      {
        Clock::time_point at;
        {
          std::lock_guard lock(mutex);
          at = std::max(Clock::now(), due);
          due = at + std::chrono::duration_cast<Clock::duration>(std::chrono::duration<double>(double(bytes) / std::max(1u, rate)));
        }
        std::mutex waitMutex;
        std::unique_lock lock(waitMutex);
        std::condition_variable_any wake;
        wake.wait_until(lock, stop, at, [] { return false; });
        return !stop.stop_requested();
      }
    } uploadBudget, downloadBudget;

    // Context and I/O buffers must survive HANDLE_CLOSING, not just CloseHandle.
    struct Request
    {
      HINTERNET handle{};
      std::mutex mutex;
      std::condition_variable_any changed;
      DWORD event{}, count{}, error{};
      bool closing{}, callback{};
      static void CALLBACK Status(HINTERNET, DWORD_PTR context, DWORD status, void* data, DWORD length)
      {
        if (!context) return;
        auto& self = *reinterpret_cast<Request*>(context);
        std::lock_guard lock(self.mutex);
        if (status == WINHTTP_CALLBACK_STATUS_HANDLE_CLOSING) self.closing = true;
        else if (status == WINHTTP_CALLBACK_STATUS_REQUEST_ERROR)
          self.error = static_cast<WINHTTP_ASYNC_RESULT*>(data)->dwError;
        else if (status == WINHTTP_CALLBACK_STATUS_SENDREQUEST_COMPLETE || status == WINHTTP_CALLBACK_STATUS_HEADERS_AVAILABLE ||
                 status == WINHTTP_CALLBACK_STATUS_READ_COMPLETE || status == WINHTTP_CALLBACK_STATUS_WRITE_COMPLETE)
        {
          self.event = status;
          self.count = status == WINHTTP_CALLBACK_STATUS_WRITE_COMPLETE ? *static_cast<DWORD*>(data) : length;
        }
        self.changed.notify_all();
      }
      ~Request()
      {
        if (!handle) return;
        WinHttpCloseHandle(handle);
        if (callback)
        {
          std::unique_lock lock(mutex);
          changed.wait(lock, [this] { return closing; });
        }
      }
      bool Attach()
      {
        auto context = reinterpret_cast<DWORD_PTR>(this);
        if (!WinHttpSetOption(handle, WINHTTP_OPTION_CONTEXT_VALUE, &context, sizeof(context))) return false;
        callback = WinHttpSetStatusCallback(handle, Status, WINHTTP_CALLBACK_FLAG_ALL_COMPLETIONS | WINHTTP_CALLBACK_FLAG_HANDLES, 0)
                   != WINHTTP_INVALID_STATUS_CALLBACK;
        return callback;
      }
      template<class Call> bool Step(std::stop_token stop, DWORD expected, Call&& call)
      {
        if (stop.stop_requested()) return false;
        { std::lock_guard lock(mutex); event = count = error = 0; }
        if (!call()) { const auto failure = GetLastError(); std::lock_guard lock(mutex); error = failure; return false; }
        std::unique_lock lock(mutex);
        return changed.wait(lock, stop, [this, expected] { return event == expected || error; }) && !error;
      }
    };
    struct Job
    {
      Wire::Transfer transfer;
      std::shared_ptr<const Bytes> input;
      std::shared_ptr<Bytes> output;
      std::atomic<std::uint32_t> progress{};
      std::optional<std::string> error;
      std::atomic<bool> done{};
      std::jthread thread;
    };
    Handle session{WinHttpOpen(L"Dreamsleeve", WINHTTP_ACCESS_TYPE_NO_PROXY, WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, WINHTTP_FLAG_ASYNC)};
    std::vector<std::unique_ptr<Job>> jobs;
    std::string origin;
    bool allowInsecure{};

    std::optional<std::string> Run(Job& job, std::string url, bool insecure, std::uint32_t rate, std::stop_token stop, Trace& trace)
    {
      if (auto valid = Auth::ValidateUrl(url, insecure); !valid) return valid.error();
      if (!session) return "WinHTTP session unavailable";
      int length = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, url.data(), static_cast<int>(url.size()), nullptr, 0);
      if (!length) return "HTTP URL encoding";
      std::wstring wide(length, L'\0');
      if (!MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, url.data(), static_cast<int>(url.size()), wide.data(), length)) return "HTTP URL encoding";
      URL_COMPONENTS parts{};
      parts.dwStructSize = sizeof(parts); parts.dwHostNameLength = DWORD(-1);
      if (!WinHttpCrackUrl(wide.c_str(), static_cast<DWORD>(wide.size()), 0, &parts)) return "HTTP URL";
      std::wstring host(parts.lpszHostName, parts.dwHostNameLength);
      Handle connection{WinHttpConnect(session.get(), host.c_str(), parts.nPort, 0)};
      if (!connection) return "HTTP connection";
      std::array<std::uint8_t, 1> tail{};
      // Buffers belong to job; request drains callbacks before Run returns.
      Request request;
      request.handle = WinHttpOpenRequest(connection.get(), job.transfer.upload ? L"PUT" : L"GET", L"/phantoms/content",
                                         nullptr, WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES,
                                         parts.nScheme == INTERNET_SCHEME_HTTPS ? WINHTTP_FLAG_SECURE : 0);
      if (!request.handle || !request.Attach()) return "HTTP request";
      DWORD redirects = WINHTTP_OPTION_REDIRECT_POLICY_NEVER;
      DWORD disabled = WINHTTP_DISABLE_COOKIES | WINHTTP_DISABLE_AUTHENTICATION;
      if (!WinHttpSetTimeouts(request.handle, 5000, 5000, 30000, 30000) ||
          !WinHttpSetOption(request.handle, WINHTTP_OPTION_REDIRECT_POLICY, &redirects, sizeof(redirects)) ||
          !WinHttpSetOption(request.handle, WINHTTP_OPTION_DISABLE_FEATURE, &disabled, sizeof(disabled))) return "HTTP options";
      std::wstring headers = L"Authorization: Bearer " + std::wstring(job.transfer.httpToken.begin(), job.transfer.httpToken.end()) + L"\r\nContent-Type: application/octet-stream\r\n";
      const auto fail = [&] { std::lock_guard lock(request.mutex); return stop.stop_requested() ? std::string("HTTP canceled") : "WinHTTP error " + std::to_string(request.error); };
      if (!trace.Step(0, "send", [&] { return request.Step(stop, WINHTTP_CALLBACK_STATUS_SENDREQUEST_COMPLETE, [&] {
            return WinHttpSendRequest(request.handle, headers.c_str(), static_cast<DWORD>(headers.size()), nullptr, 0,
                                      job.transfer.upload ? job.transfer.BodyBytes() : 0, reinterpret_cast<DWORD_PTR>(&request));
          }); })) return fail();
      if (job.transfer.upload)
      {
        while (job.progress < job.input->size())
        {
          auto offset = job.progress.load();
          auto count = static_cast<DWORD>(std::min<std::size_t>(32768, job.input->size() - offset));
          if (!trace.Step(2, "budget", [&] { return uploadBudget.Wait(count, rate, stop); })) return "HTTP canceled";
          if (!trace.Step(3, "write", [&] { return request.Step(stop, WINHTTP_CALLBACK_STATUS_WRITE_COMPLETE, [&] {
                return WinHttpWriteData(request.handle, job.input->data() + offset, count, nullptr);
              }); })) return fail();
          if (!request.count || request.count > count) return "HTTP write length";
          job.progress = offset + request.count;
          trace.Progress(job.progress.load());
        }
      }
      if (!trace.Step(1, "headers", [&] { return request.Step(stop, WINHTTP_CALLBACK_STATUS_HEADERS_AVAILABLE, [&] { return WinHttpReceiveResponse(request.handle, nullptr); }); })) return fail();
      DWORD status{}, statusSize = sizeof(status);
      if (!WinHttpQueryHeaders(request.handle, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER, nullptr, &status, &statusSize, nullptr)) return "HTTP status";
      if (status != (job.transfer.upload ? 204u : 200u)) return "HTTP status " + std::to_string(status);
      if (job.transfer.upload) return {};
      DWORD contentLength{}, headerSize = sizeof(contentLength);
      if (!WinHttpQueryHeaders(request.handle, WINHTTP_QUERY_CONTENT_LENGTH | WINHTTP_QUERY_FLAG_NUMBER, nullptr, &contentLength, &headerSize, nullptr) ||
          contentLength != job.transfer.BodyBytes()) return "HTTP content length";
      job.output = std::make_shared<Bytes>(contentLength);
      while (job.progress < contentLength)
      {
        auto offset = job.progress.load();
        auto count = std::min<DWORD>(32768, contentLength - offset);
        if (!trace.Step(2, "budget", [&] { return downloadBudget.Wait(count, rate, stop); })) return "HTTP canceled";
        if (!trace.Step(3, "read", [&] { return request.Step(stop, WINHTTP_CALLBACK_STATUS_READ_COMPLETE, [&] {
              return WinHttpReadData(request.handle, job.output->data() + offset, count, nullptr);
            }); })) return fail();
        if (!request.count || request.count > count) return "HTTP truncated";
        job.progress = offset + request.count;
          trace.Progress(job.progress.load());
      }
      if (!trace.Step(4, "eof", [&] { return request.Step(stop, WINHTTP_CALLBACK_STATUS_READ_COMPLETE, [&] { return WinHttpReadData(request.handle, tail.data(), 1, nullptr); }); })) return fail();
      if (request.count) return "HTTP oversized";
      return {};
    }
  public:
    struct Completion { Wire::Transfer transfer; std::shared_ptr<Bytes> bytes; std::optional<std::string> error; };
    ~Http() { Reset(); jobs.clear(); }
    void Configure(std::string url, bool insecure) { origin = std::move(url); allowInsecure = insecure; }
    bool Start(Wire::Transfer transfer, std::shared_ptr<const Bytes> input, std::uint32_t rate)
    {
      if (jobs.size() >= 8 || origin.empty() || !rate || (transfer.upload && (!input || input->size() != transfer.BodyBytes()))) return false;
      auto job = std::make_unique<Job>();
      job->transfer = std::move(transfer); job->input = std::move(input);
      auto* owner = job.get();
      job->thread = std::jthread([this, owner, url = origin, insecure = allowInsecure, rate](std::stop_token stop) {
        Trace trace{owner->transfer};
        owner->error = Run(*owner, url, insecure, rate, stop, trace);
        trace.Emit(owner->error ? (stop.stop_requested() ? "canceled" : "failed") : "complete", 0);
        owner->done.store(true, std::memory_order_release);
      });
      jobs.push_back(std::move(job));
      return true;
    }
    void Cancel(TransferId id) { for (auto& job : jobs) if (job->transfer.transfer == id) job->thread.request_stop(); }
    void Reset() { for (auto& job : jobs) job->thread.request_stop(); }
    std::uint32_t Progress(TransferId id, RequestId request) const
    { for (const auto& job : jobs) if (job->transfer.transfer == id && job->transfer.request == request) return job->progress.load(); return 0; }
    std::vector<Completion> Poll()
    {
      std::vector<Completion> output;
      for (auto it = jobs.begin(); it != jobs.end();)
      {
        if (!(*it)->done.load(std::memory_order_acquire)) { ++it; continue; }
        auto& job = **it;
        output.push_back({job.transfer, std::move(job.output), std::move(job.error)});
        it = jobs.erase(it);
      }
      return output;
    }
  };
}
