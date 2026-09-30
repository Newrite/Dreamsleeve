export module Dreamsleeve.Client.Application;

import std;
export import Dreamsleeve.Client.Auth;
import DreamNet.Runtime;
import Dreamsleeve.Client.CredentialStore;
export import Dreamsleeve.Client.Settings;
export import Dreamsleeve.Client.Runtime;

export namespace Dreamsleeve::Client
{

  // All public calls, including destruction, belong to the application main
  // thread. The worker owns Runtime; Exchange is the only shared-state boundary.
  class ClientApplication final
  {
public:

    using Result = std::expected<std::unique_ptr<ClientApplication>, std::string>;
    using Ptr    = std::unique_ptr<ClientApplication>;

    static Result TryCreate(ClientSettings settings)
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
      try
      {
        app->worker = std::jthread(&ClientApplication::Run, app.get());
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
    }

private:

    ClientApplication(ClientSettings options, DreamNetRuntime net, ClientExchange::Ptr boundary, ClientRuntime::Ptr client)
        : settings(std::move(options)),
          enet(std::move(net)),
          exchange(std::move(boundary)),
          runtime(std::move(client))
    {}

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
      auto result = Auth::Logout(settings.authUrl, (**saved).token, settings.allowInsecureRemoteAuth);
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
        auto registered =
          Auth::RegisterAccount(settings.authUrl, request.credentials, *request.registerName, settings.allowInsecureRemoteAuth);
        if (exchange->AuthenticationCanceled()) return {};
        if (!registered) return registered;
      }
      return ConnectGrant(
        Auth::LoginGrant(settings.authUrl, request.credentials, request.remember, settings.allowInsecureRemoteAuth),
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
      auto grant = Auth::Resume(settings.authUrl, (**saved).token, settings.allowInsecureRemoteAuth);
      if (!grant && grant.error().code == Auth::FailureCode::InvalidCredentials)
      {
        if (auto forgotten = ForgetLogin(); !forgotten) return forgotten;
      }
      return ConnectGrant(std::move(grant), false);
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
      auto result = Auth::ResetPassword(settings.authUrl, request.code, request.password, settings.allowInsecureRemoteAuth);
      if (!result) return result;
      return ForgetLogin();
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
        auto control = exchange->TakeControl();
        if (control.authentication)
        {
          // A new sign-in: the notice of how the last one ended is spent.
          exchange->PublishSessionEnd(std::nullopt);
          auto result = exchange->AuthenticationCanceled()
                        ? AuthResult{}
                        : std::visit([this](const auto& request) { return Authenticate(request); }, *control.authentication);
          if (!result && result.error().ban) exchange->PublishSessionEnd(result.error().ban);
          exchange->CompleteAuthentication(
            result ? std::string{} : std::move(result.error().message),
            result ? Auth::FailureCode::None : result.error().code);
        }
        if (exchange->StopRequested()) break;
        if (control.disconnect || (control.authentication && exchange->AuthenticationCanceled())) Report(runtime->Disconnect());
        Report(runtime->Poll(10));

        if (SessionIdle(runtime->Phase())) exchange->WaitForControl();
      }

      CloseSession(true);
      runtime.reset();
      exchange->Finish();
    }

    ClientSettings      settings;
    DreamNetRuntime     enet;
    ClientExchange::Ptr exchange;
    ClientRuntime::Ptr  runtime;
    std::jthread        worker;
  };

}
