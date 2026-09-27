export module Dreamsleeve.Client.Application;

import std;
export import Dreamsleeve.Client.Auth;
import DreamNet.Runtime;
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

    static Result TryCreate(ClientSettings settings)
    {
      if (auto valid = ValidateClientSettings(settings); !valid) return std::unexpected{valid.error()};
      auto net = DreamNetRuntime::TryInitialize();
      if (!net) return std::unexpected{net.error().ToLogString()};
      auto exchange = ClientExchange::TryCreate(settings.commandCapacity, settings.stateCapacity);
      if (!exchange) return std::unexpected{"Cannot create client exchange"};
      auto runtime = ClientRuntime::TryCreate(settings.client, **exchange);
      if (!runtime) return std::unexpected{Describe(runtime.error())};

      auto app = std::unique_ptr<ClientApplication>{
          new ClientApplication{std::move(settings), std::move(*net), std::move(*exchange), std::move(*runtime)}
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

    std::expected<void, std::string> Connect(Credentials credentials, std::optional<std::string> registerName = std::nullopt)
    {
      return exchange->PostLogin(std::move(credentials), std::move(registerName));
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

    static std::string Describe(const ClientRuntime::Error& error)
    {
      return std::visit(
        [](const auto& value) -> std::string {
          if constexpr (requires { value.ToLogString(); })
            return value.ToLogString();
          else
            return "Client error " + std::to_string(static_cast<int>(value.code)) + ": " + value.field;
        },
        error);
    }

    void Report(const ClientRuntime::Result<void>& result)
    {
      if (!result) exchange->PublishError(Describe(result.error()));
    }

    std::expected<void, std::string> Authenticate(const LoginRequest& request)
    {
      if (exchange->LoginCanceled()) return {};
      if (request.registerName)
      {
        auto registered = Auth::Register(settings.authUrl, request.credentials, *request.registerName);
        if (exchange->LoginCanceled()) return {};
        if (!registered) return std::unexpected{registered.error()};
      }

      auto ticket = Auth::Login(settings.authUrl, request.credentials);
      if (exchange->LoginCanceled()) return {};
      if (!ticket) return std::unexpected{ticket.error()};
      auto connected = runtime->Connect(std::move(*ticket));
      if (!connected) return std::unexpected{Describe(connected.error())};
      return {};
    }

    static void Run(ClientApplication* self)
    {
      self->RunLoop();
    }

    void RunLoop()
    {
      while (!exchange->StopRequested())
      {
        auto control = exchange->TakeControl();
        if (control.login)
        {
          auto result = Authenticate(*control.login);
          exchange->CompleteLogin(result ? std::string{} : std::move(result.error()));
        }
        if (exchange->StopRequested()) break;
        if (control.disconnect || (control.login && exchange->LoginCanceled())) Report(runtime->Disconnect());
        Report(runtime->Poll(10));

        if (runtime->Phase() == SessionPhase::Disconnected || runtime->Phase() == SessionPhase::Faulted) exchange->WaitForControl();
      }

      Report(runtime->Disconnect());
      while (runtime->Phase() == SessionPhase::Disconnecting)
        Report(runtime->Poll(10));
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
