module;
#include "AuthHttp.h"

export module Dreamsleeve.Client.Application;

import std;
import DreamNet.Runtime;
export import Dreamsleeve.Client.Settings;
export import Dreamsleeve.Client.Runtime;

export namespace Dreamsleeve::Client
{
  using Credentials = Auth::Credentials;

  struct ApplicationStatus
  {
    SessionPhase phase{SessionPhase::Disconnected};
    bool authenticating{};
    bool stopped{};
    std::string error;
    bool operator==(const ApplicationStatus&) const = default;
  };

  // One application owner calls Connect/Disconnect/Stop; the game/UI consumer
  // reads Status and the existing Exchange. Status callbacks are worker-side
  // diagnostics only: do not block, throw or destroy/stop this object in them.
  class ClientApplication final
  {
  public:
    using Result = std::expected<std::unique_ptr<ClientApplication>, std::string>;
    using StatusHandler = std::function<void(const ApplicationStatus&)>;

    static Result TryCreate(ClientSettings settings, StatusHandler handler = {})
    {
      if (auto valid = ValidateClientSettings(settings); !valid) return std::unexpected{valid.error()};
      auto net = DreamNetRuntime::TryInitialize();
      if (!net) return std::unexpected{net.error().ToLogString()};
      auto exchange = ClientExchange::TryCreate(settings.commandCapacity, settings.stateCapacity);
      if (!exchange) return std::unexpected{"Cannot create client exchange"};
      auto runtime = ClientRuntime::TryCreate(settings.client, **exchange);
      if (!runtime) return std::unexpected{Describe(runtime.error())};

      auto app = std::unique_ptr<ClientApplication>{new ClientApplication{
        std::move(settings), std::move(*net), std::move(*exchange), std::move(*runtime), std::move(handler)}};
      try { app->worker = std::jthread(&ClientApplication::Run, app.get()); }
      catch (const std::system_error&) { return std::unexpected{"Cannot start client network thread"}; }
      return app;
    }

    ~ClientApplication() { Stop(); }
    ClientApplication(const ClientApplication&) = delete;
    ClientApplication& operator=(const ClientApplication&) = delete;

    ClientExchange& Exchange() noexcept { return *exchange; }
    const ClientSettings& Settings() const noexcept { return settings; }

    ApplicationStatus Status() const
    {
      std::lock_guard lock(mutex);
      return status;
    }

    // Registration, when requested, precedes login. Each connect needs fresh
    // credentials; no password/ticket is retained for automatic reconnect.
    std::expected<void, std::string> Connect(Credentials credentials, std::optional<std::string> registerName = std::nullopt)
    {
      std::lock_guard lock(mutex);
      if (closed) return std::unexpected{"Client application is stopped"};
      if (status.authenticating || disconnectRequested ||
          (status.phase != SessionPhase::Disconnected && status.phase != SessionPhase::Faulted))
        return std::unexpected{"A connection operation or session is already active"};

      canceled = false;
      status.authenticating = true;
      status.error.clear();
      login.emplace(Login{std::move(credentials), std::move(registerName)});
      wake.notify_one();
      return {};
    }

    void Disconnect()
    {
      std::lock_guard lock(mutex);
      if (closed) return;
      canceled = true;
      disconnectRequested = true;
      wake.notify_one();
    }

    // Final shutdown; create a new application to restart after Stop.
    // An in-flight synchronous HTTP call finishes within its configured timeout.
    void Stop()
    {
      {
        std::lock_guard lock(mutex);
        closed = true;
        canceled = true;
      }
      worker.request_stop();
      wake.notify_one();
      if (worker.joinable()) worker.join();
    }

  private:
    struct Login
    {
      Credentials credentials;
      std::optional<std::string> registerName;
    };

    ClientApplication(ClientSettings options, DreamNetRuntime net, ClientExchange::Ptr boundary,
                      ClientRuntime::Ptr client, StatusHandler callback)
      : settings(std::move(options)), enet(std::move(net)), exchange(std::move(boundary)),
        runtime(std::move(client)), onStatus(std::move(callback)) {}

    static std::string Describe(const ClientRuntime::Error& error)
    {
      return std::visit([](const auto& value) -> std::string {
        if constexpr (requires { value.ToLogString(); }) return value.ToLogString();
        else return "Client error " + std::to_string(static_cast<int>(value.code)) + ": " + value.field;
      }, error);
    }

    void Publish(bool authenticating = false, std::string error = {})
    {
      ApplicationStatus snapshot;
      {
        std::lock_guard lock(mutex);
        status.phase = runtime->Phase();
        // A UI connect can arrive just after Poll; do not erase its busy flag.
        status.authenticating = authenticating || login.has_value();
        if (!error.empty()) status.error = std::move(error);
        snapshot = status;
      }
      if (snapshot != reported)
      {
        reported = snapshot;
        if (onStatus) onStatus(snapshot);
      }
    }

    void Report(const ClientRuntime::Result<void>& result)
    {
      Publish(false, result ? std::string{} : Describe(result.error()));
    }

    void Authenticate(Login& request)
    {
      Publish(true);
      if (request.registerName && !canceled)
      {
        auto registered = Auth::Register(settings.authUrl, request.credentials, *request.registerName);
        if (canceled) { Publish(); return; }
        if (!registered)
        {
          Publish(false, registered.error());
          return;
        }
      }
      if (canceled) { Publish(); return; }
      auto ticket = Auth::Login(settings.authUrl, request.credentials);
      if (canceled) { Publish(); return; }
      if (!ticket) Publish(false, ticket.error());
      else Report(runtime->Connect(std::move(*ticket)));
    }

    static void Run(std::stop_token stop, ClientApplication* self)
    {
      self->RunLoop(stop);
    }

    void RunLoop(std::stop_token stop)
    {
      while (!stop.stop_requested())
      {
        std::optional<Login> request;
        bool disconnect{};
        {
          std::lock_guard lock(mutex);
          request.swap(login);
          disconnect = std::exchange(disconnectRequested, false);
        }
        if (request) Authenticate(*request);
        if (disconnect || canceled) Report(runtime->Disconnect());
        Report(runtime->Poll(10));

        if (runtime->Phase() == SessionPhase::Disconnected || runtime->Phase() == SessionPhase::Faulted)
        {
          std::unique_lock lock(mutex);
          wake.wait_for(lock, std::chrono::milliseconds{10}, [&] {
            return stop.stop_requested() || login.has_value() || disconnectRequested;
          });
        }
      }

      Report(runtime->Disconnect());
      while (runtime->Phase() == SessionPhase::Disconnecting) Report(runtime->Poll(10));
      runtime.reset();
      exchange->Finish();
      ApplicationStatus snapshot;
      {
        std::lock_guard lock(mutex);
        login.reset();
        status.authenticating = false;
        status.stopped = true;
        snapshot = status;
      }
      if (onStatus) onStatus(snapshot);
    }

    ClientSettings settings;
    DreamNetRuntime enet;
    ClientExchange::Ptr exchange;
    ClientRuntime::Ptr runtime;
    StatusHandler onStatus;
    mutable std::mutex mutex;
    std::condition_variable wake;
    std::optional<Login> login;
    bool disconnectRequested{};
    bool closed{};
    std::atomic_bool canceled{};
    ApplicationStatus status;
    ApplicationStatus reported;
    std::jthread worker;
  };
}
