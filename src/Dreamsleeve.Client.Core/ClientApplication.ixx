export module Dreamsleeve.Client.Application;

import std;
export import Dreamsleeve.Client.Auth;
import DreamNet.Runtime;
import Dreamsleeve.Client.CredentialStore;
import Dreamsleeve.Client.Device;
export import Dreamsleeve.Client.Settings;
export import Dreamsleeve.Client.Runtime;

export namespace Dreamsleeve::Client
{

  // How the application picks its route (RoutesOf): the one the player chose,
  // or automatically, starting with first (the one that worked last time).
  struct RoutePreference
  {
    std::optional<std::size_t> chosen;
    std::size_t                first{};
  };

  // All public calls, including destruction, belong to the application main
  // thread. The worker owns Runtime; Exchange is the only shared-state boundary.
  class ClientApplication final
  {
public:

    using Result = std::expected<std::unique_ptr<ClientApplication>, std::string>;
    using Ptr    = std::unique_ptr<ClientApplication>;

    static Result TryCreate(ClientSettings settings, RoutePreference preference = {})
    {
      if (auto valid = ValidateClientSettings(settings); !valid) return std::unexpected{valid.error()};
      auto net = DreamNetRuntime::TryInitialize();
      if (!net) return std::unexpected{net.error().ToLogString()};
      auto exchange = ClientExchange::TryCreate(settings.commandCapacity, settings.stateCapacity);
      if (!exchange) return std::unexpected{"Cannot create client exchange"};
      auto runtime = ClientRuntime::Create(settings.client, **exchange);

      auto app = std::unique_ptr<ClientApplication>{
          new ClientApplication{std::move(settings), std::move(*net), std::move(*exchange), std::move(runtime)}
      };
      // Before the threads: the first connection already goes by the route.
      const auto known = [&](std::optional<std::size_t> index) { return index && *index < app->routes.size() ? index : std::nullopt; };
      const auto chosen = known(preference.chosen);
      app->exchange->SetRouteChoice(chosen);
      app->automatic = !chosen;
      app->SelectRoute(chosen.value_or(known(preference.first).value_or(0)), true);
      try
      {
        app->methodsReader = std::jthread([self = app.get()](std::stop_token stop) { self->ReadMethods(stop); });
        app->worker        = std::jthread(&ClientApplication::Run, app.get());
      }
      catch (const std::system_error&)
      {
        return std::unexpected{"Cannot start client network thread"};
      }
      return app;
    }

    ~ClientApplication()
    {
      Stop();
    }

    ClientApplication(const ClientApplication&)            = delete;
    ClientApplication& operator=(const ClientApplication&) = delete;

    ClientExchange& Exchange() noexcept
    {
      return *exchange;
    }

    const ClientSettings& Settings() const noexcept
    {
      return settings;
    }

    // RoutesOf the settings; Status().route is an index of them.
    std::span<const ConnectionRoute> Routes() const noexcept
    {
      return routes;
    }

    ClientStatus Status() const
    {
      return exchange->Status();
    }

    std::expected<void, std::string> Connect(
      Credentials                credentials,
      std::optional<std::string> registerName = std::nullopt,
      bool                       remember     = false)
    {
      return exchange->PostLogin(std::move(credentials), std::move(registerName), remember);
    }

    std::expected<void, std::string> ConnectSaved()
    {
      return exchange->PostAuthentication(ResumeLogin{});
    }

    // The browser opens Steam; the sign-in completes when the player returns.
    std::expected<void, std::string> ConnectSteam(bool remember = true)
    {
      return exchange->PostAuthentication(SteamLogin{remember});
    }

    std::expected<void, std::string> SignOut()
    {
      return exchange->PostAuthentication(SignOutAccount{});
    }

    // Local removal works offline; it does not revoke a copied server credential.
    std::expected<void, std::string> ForgetSavedLogin()
    {
      return exchange->PostAuthentication(Dreamsleeve::Client::ForgetLogin{});
    }

    std::expected<void, std::string> ResetPassword(std::string code, std::string password)
    {
      return exchange->PostAuthentication(ResetAccountPassword{std::move(code), std::move(password)});
    }

    void Disconnect()
    {
      exchange->RequestDisconnect();
    }

    // Main thread only; final and idempotent. HTTP cancellation is observed
    // after its current system call returns. No lock is held while joining.
    void Stop()
    {
      exchange->RequestStop();
      if (worker.joinable()) worker.join();
      methodsReader.request_stop();
      if (methodsReader.joinable()) methodsReader.join();
    }

private:

    // A Steam sign-in asks the server this often whether the browser finished,
    // and gives up after this many failed asks in a row.
    static constexpr auto SteamPollInterval    = std::chrono::seconds{2};
    static constexpr int  MaxSteamPollFailures = 5;
    // Unknown sign-in methods are asked again this often.
    static constexpr auto MethodsRetry = std::chrono::seconds{30};

    // How opening the browser ended, written by its own thread.
    struct BrowserOpening
    {
      std::mutex  mutex;
      bool        done{};
      std::string opener;
      std::string error;
    };

    ClientApplication(ClientSettings options, DreamNetRuntime net, ClientExchange::Ptr boundary, ClientRuntime::Ptr client)
        : settings(std::move(options)),
          routes(RoutesOf(settings)),
          enet(std::move(net)),
          exchange(std::move(boundary)),
          runtime(std::move(client))
    {
      // One server for the application's lifetime: its credential scope keys the device hash too.
      if (auto scope = Auth::CredentialTarget(settings.authUrl, settings.allowInsecureRemoteAuth)) device = Device::Identify(*scope);
    }

    // Status text shown to the user: code and message only. ToLogString() adds
    // source locations and full function signatures, which belong in logs.
    static std::string Describe(const ClientRuntime::Error& error)
    {
      return std::visit(
        [](const auto& value) -> std::string {
          if constexpr (std::is_same_v<std::decay_t<decltype(value)>, DreamNetError>)
          {
            std::string text{value.CodeName()};
            if (!value.message.empty()) text += ": " + value.message;
            if (const auto* cause = value.Cause())
            {
              text += " (";
              text += cause->CodeName();
              if (!cause->message.empty()) text += ": " + cause->message;
              text += ")";
            }
            return text;
          }
          else
            return "Client error " + std::to_string(static_cast<int>(value.code)) + ": " + value.field;
        },
        error);
    }

    void Report(const ClientRuntime::Result<void>& result)
    {
      if (!result) exchange->PublishError(Describe(result.error()));
    }

    // The route the traffic goes by. The saved login and the device stay with
    // settings.authUrl, the main route's, whichever route answers.
    const ConnectionRoute& Route() const
    {
      return routes[active.load()];
    }

    void SelectRoute(std::size_t index, bool now)
    {
      active       = index;
      routeReached = false;
      runtime->UseEndpoint(routes[index].serverHost, routes[index].serverPort, now);
      exchange->PublishRoute(index, false);
      RefreshMethods();
    }

    // The player's choice applies while no session runs.
    void FollowRouteChoice()
    {
      const auto choice = exchange->RouteChoice();
      automatic         = !choice;
      if (choice && *choice < routes.size() && *choice != active.load() && SessionIdle(runtime->Phase())) SelectRoute(*choice, true);
    }

    // The active route did not get through: chosen automatically, the next one
    // takes over, at once until every route failed in a row, then after the
    // guest's growing wait. false when there is no other route to take.
    bool NextRoute()
    {
      if (!automatic || routes.size() < 2) return false;
      ++failedRoutes;
      SelectRoute((active.load() + 1) % routes.size(), failedRoutes < routes.size());
      return true;
    }

    void NoteReached()
    {
      failedRoutes = 0;
      if (routeReached) return;
      routeReached = true;
      exchange->PublishRoute(active.load(), true);
    }

    // A request by the active route; while it goes unanswered and the route is
    // chosen automatically, by each other route in turn. Any answer keeps the route.
    template <class Request>
    auto OnRoutes(Request request)
    {
      auto       result      = request(Route());
      const auto unanswered  = [&] { return !result && result.error().code == Auth::FailureCode::Unreachable; };
      for (std::size_t tried = 1; unanswered() && tried < routes.size() && !exchange->AuthenticationCanceled() && NextRoute(); ++tried)
        result = request(Route());
      if (!unanswered()) NoteReached();
      return result;
    }

    // Closes the session and serves the transport until the close completes.
    // The client stays a guest unless it leaves: the application stops.
    void CloseSession(bool leave = false)
    {
      if (leave) runtime->KeepGuest(false);
      Report(runtime->Disconnect());
      while (runtime->Closing())
        Report(runtime->Poll(10));
    }

    using AuthResult = std::expected<void, Auth::Failure>;

    AuthResult ForgetLogin()
    {
      auto result = CredentialStore::Forget(settings.authUrl, settings.allowInsecureRemoteAuth);
      if (result) exchange->PublishSavedLogin(false);
      return result;
    }

    AuthResult SignOutSaved()
    {
      auto saved = CredentialStore::Load(settings.authUrl, settings.allowInsecureRemoteAuth);
      if (!saved) return std::unexpected{saved.error()};
      if (!*saved)
      {
        exchange->PublishSavedLogin(false);
        return {};
      }
      auto result = OnRoutes([&](const ConnectionRoute& route) {
        return Auth::Logout(route.authUrl, (**saved).token, settings.allowInsecureRemoteAuth);
      });
      // Keep the credential on transient failure so the UI can retry revocation.
      // ForgetSavedLogin is the explicit offline alternative.
      if (!result) return result;
      return ForgetLogin();
    }

    AuthResult ConnectGrant(Auth::GrantResult grant, bool remember)
    {
      if (exchange->AuthenticationCanceled()) return {};
      if (!grant) return std::unexpected{grant.error()};
      if (remember)
      {
        auto saved = CredentialStore::Save(settings.authUrl, {grant->username, grant->rememberToken}, settings.allowInsecureRemoteAuth);
        if (!saved) return saved;
        exchange->PublishSavedLogin(true, grant->username);
      }
      auto connected = runtime->Connect(std::move(grant->sessionTicket));
      if (!connected)
        return std::unexpected{
            Auth::Failure{Auth::FailureCode::Unavailable, Describe(connected.error())}
        };
      return {};
    }

    AuthResult Authenticate(const PasswordLogin& request)
    {
      if (request.registerName)
      {
        // Not repeated by another route: an answer lost on the way may have
        // registered the account already. The player's next try goes by the next one.
        auto registered =
          Auth::RegisterAccount(Route().authUrl, request.credentials, *request.registerName, settings.allowInsecureRemoteAuth, device);
        if (exchange->AuthenticationCanceled()) return {};
        if (!registered && registered.error().code == Auth::FailureCode::Unreachable) NextRoute();
        if (!registered) return registered;
        NoteReached();
      }
      return ConnectGrant(
        OnRoutes([&](const ConnectionRoute& route) {
          return Auth::LoginGrant(route.authUrl, request.credentials, request.remember, settings.allowInsecureRemoteAuth, device);
        }),
        request.remember);
    }

    AuthResult Authenticate(const ResumeLogin&)
    {
      auto saved = CredentialStore::Load(settings.authUrl, settings.allowInsecureRemoteAuth);
      if (!saved) return std::unexpected{saved.error()};
      if (!*saved)
      {
        exchange->PublishSavedLogin(false);
        return std::unexpected{
            Auth::Failure{Auth::FailureCode::InvalidCredentials, "Sign in to this server first"}
        };
      }
      auto grant = OnRoutes([&](const ConnectionRoute& route) {
        return Auth::Resume(route.authUrl, (**saved).token, settings.allowInsecureRemoteAuth, device);
      });
      if (!grant && grant.error().code == Auth::FailureCode::InvalidCredentials)
      {
        if (auto forgotten = ForgetLogin(); !forgotten) return forgotten;
      }
      return ConnectGrant(std::move(grant), false);
    }

    // The player signs in in the browser meanwhile; the guest session is
    // served while the client waits, and a cancel or stop ends the wait.
    AuthResult Authenticate(const SteamLogin& request)
    {
      // The flow stays on the route it began by: the server sends the browser back there.
      auto flow = OnRoutes([&](const ConnectionRoute& route) {
        return Auth::BeginSteam(route.authUrl, request.remember, settings.allowInsecureRemoteAuth, device);
      });
      if (!flow) return std::unexpected{flow.error()};
      // The page stays available for "copy the link" while the client waits.
      exchange->PublishSteamPage(flow->page);
      struct PageShown
      {
        ClientExchange& exchange;

        ~PageShown()
        {
          exchange.PublishSteamPage({});
        }
      } shown{*exchange};
      // The shell may take its time or never answer, and the wait must not
      // stall with it. The thread holds only its own copies, so it is detached.
      auto opening = std::make_shared<BrowserOpening>();
      try
      {
        std::thread{[opening, page = flow->page] {
          auto            opened = Auth::OpenSteamPage(page);
          std::lock_guard lock{opening->mutex};
          if (opened)
            opening->opener = std::move(*opened);
          else
            opening->error = std::move(opened.error());
          opening->done = true;
        }}.detach();
      }
      catch (const std::system_error&)
      {
        opening->done  = true;
        opening->error = "Cannot start a thread for the browser";
      }
      bool       opened{};
      const auto deadline = std::chrono::steady_clock::now() + flow->lifetime;
      auto       next     = std::chrono::steady_clock::now() + SteamPollInterval;
      int        failures{};
      while (!exchange->AuthenticationCanceled())
      {
        Report(runtime->Poll(10));
        if (!opened)
        {
          std::lock_guard lock{opening->mutex};
          opened = opening->done;
          if (opened) exchange->PublishSteamPage(flow->page, opening->opener, opening->error);
        }
        const auto now = std::chrono::steady_clock::now();
        if (now >= deadline)
          return std::unexpected{
              Auth::Failure{Auth::FailureCode::SteamExpired, "The Steam sign-in was not finished in time"}
          };
        if (now < next) continue;
        next        = now + SteamPollInterval;
        auto polled = Auth::PollSteam(Route().authUrl, flow->flow, flow->secret, settings.allowInsecureRemoteAuth);
        if (!polled)
        {
          // A dropped request or a busy server may pass while the browser finishes.
          const auto code = polled.error().code;
          if (
            (code == Auth::FailureCode::Unavailable || code == Auth::FailureCode::Unreachable || code == Auth::FailureCode::Busy) &&
            ++failures < MaxSteamPollFailures)
            continue;
          return std::unexpected{polled.error()};
        }
        failures = 0;
        if (!*polled) continue;
        if (request.remember && (**polled).rememberToken.empty())
          return std::unexpected{
              Auth::Failure{Auth::FailureCode::InvalidResponse, "Server did not issue a saved login token"}
          };
        return ConnectGrant(Auth::GrantResult{std::move(**polled)}, request.remember);
      }
      return {};
    }

    AuthResult Authenticate(const SignOutAccount&)
    {
      CloseSession();
      return SignOutSaved();
    }

    AuthResult Authenticate(const Dreamsleeve::Client::ForgetLogin&)
    {
      return ForgetLogin();
    }

    AuthResult Authenticate(const ResetAccountPassword& request)
    {
      auto result = OnRoutes([&](const ConnectionRoute& route) {
        return Auth::ResetPassword(route.authUrl, request.code, request.password, settings.allowInsecureRemoteAuth);
      });
      if (!result) return result;
      return ForgetLogin();
    }

    // What the server offers is asked off the network thread, so a slow server
    // never delays a sign-in: at start, after a refused sign-in, on a new route
    // and while unknown. When the server does not answer, the last known methods stay.
    void ReadMethods(std::stop_token stop)
    {
      std::unique_lock lock{methodsMutex};
      while (!stop.stop_requested())
      {
        methodsWanted = false;
        lock.unlock();
        auto methods = Auth::ReadMethods(routes[active.load()].authUrl, settings.allowInsecureRemoteAuth);
        if (methods) exchange->PublishMethods(*methods);
        lock.lock();
        if (methods)
          methodsWake.wait(lock, stop, [this] { return methodsWanted; });
        else
          methodsWake.wait_for(lock, stop, MethodsRetry, [this] { return methodsWanted; });
      }
    }

    void RefreshMethods()
    {
      {
        std::lock_guard lock{methodsMutex};
        methodsWanted = true;
      }
      methodsWake.notify_one();
    }

    static void Run(ClientApplication* self)
    {
      self->RunLoop();
    }

    void RunLoop()
    {
      auto saved = CredentialStore::Load(settings.authUrl, settings.allowInsecureRemoteAuth);
      if (saved)
        exchange->PublishSavedLogin(saved->has_value(), saved->has_value() ? (**saved).username : std::string{});
      else
        exchange->PublishError(saved.error().message);
      // Online from the start: the server counts a client that has not signed in.
      runtime->KeepGuest(true);

      while (!exchange->StopRequested())
      {
        FollowRouteChoice();
        auto control = exchange->TakeControl();
        if (control.authentication)
        {
          // A new sign-in: the notice of how the last one ended is spent.
          exchange->PublishSessionEnd(std::nullopt);
          auto result = exchange->AuthenticationCanceled()
                        ? AuthResult{}
                        : std::visit([this](const auto& request) { return Authenticate(request); }, *control.authentication);
          if (!result && result.error().ban) exchange->PublishSessionEnd(result.error().ban);
          // A refused sign-in may mean the server changed what it offers.
          const bool refused = !result && result.error().code != Auth::FailureCode::Canceled;
          exchange->CompleteAuthentication(
            result ? std::string{} : std::move(result.error().message),
            result ? Auth::FailureCode::None : result.error().code);
          if (refused) RefreshMethods();
        }
        if (exchange->StopRequested()) break;
        if (control.disconnect || (control.authentication && exchange->AuthenticationCanceled())) Report(runtime->Disconnect());
        Report(runtime->Poll(10));
        if (runtime->TakeUnreachable() > 0)
          NextRoute();
        else if (runtime->Reached())
          NoteReached();

        if (SessionIdle(runtime->Phase())) exchange->WaitForControl();
      }

      CloseSession(true);
      runtime.reset();
      exchange->Finish();
    }

    ClientSettings               settings;
    std::vector<ConnectionRoute> routes;
    // Written by the worker, read by the methods reader too.
    std::atomic<std::size_t> active{};
    // The worker's: the route is chosen automatically, the active one answered
    // since it was taken, routes that failed in a row.
    bool                       automatic{true};
    bool                       routeReached{};
    std::size_t                failedRoutes{};
    std::optional<std::string> device;
    DreamNetRuntime            enet;
    ClientExchange::Ptr exchange;
    ClientRuntime::Ptr  runtime;
    std::jthread        worker;
    // ReadMethods: methodsWanted under methodsMutex.
    std::mutex                  methodsMutex;
    std::condition_variable_any methodsWake;
    bool                        methodsWanted{};
    std::jthread                methodsReader;
  };

}
