export module Dreamsleeve.Client.Exchange;

import std;
export import Dreamsleeve.Client.Auth;
export import Dreamsleeve.Client.StateUpdateQueue;

export namespace Dreamsleeve::Client
{

  struct SendChat
  {
    std::uint64_t         requestId{};
    Domain::ChatChannelId channelId{};
    std::string           text;
  };

  // Published into a system channel. Settled like SendChat: a
  // ChatConfirmation, a ServerRejection or a CommandFailure.
  struct PostAnnouncement
  {
    std::uint64_t                    requestId{};
    Domain::ChatChannelId            channelId{};
    std::string                      text;
    Domain::AnnouncementKind         kind{Domain::AnnouncementKind::Announcement};
    Domain::ClientAnnouncementSource source{Domain::ClientAnnouncementSource::ThirdParty};
    std::string                      signature;  // Required for ThirdParty.
  };

  // A note where the player stands. Settled like SendChat: a
  // GroundMarkConfirmation, a ServerRejection or a CommandFailure.
  struct PlaceGroundNote
  {
    std::uint64_t               requestId{};
    std::string                 text;
    Domain::GroundMarkPlacement placement;
  };

  // The place the character died, with the killer's name or a cause; the
  // label may be empty. The client decides when a death is one.
  struct ReportDeath
  {
    std::uint64_t               requestId{};
    std::string                 label;
    Domain::GroundMarkPlacement placement;
  };

  // Only the author's own mark.
  struct RemoveGroundMark
  {
    std::uint64_t        requestId{};
    Domain::GroundMarkId markId{};
  };

  // Hide or show this player's names for the others; the server picks the
  // pseudonym. Settled by an IdentityConfirmation, a ServerRejection or a CommandFailure.
  struct SetIdentityVisibility
  {
    std::uint64_t          requestId{};
    Domain::HiddenIdentity hiding{Domain::HiddenIdentity::None};
  };

  // Complete sampled values, not a patch. Only adjacent pending samples from
  // the same session can replace one another; transitions remain ordered.
  struct LocalMovement
  {
    std::optional<Domain::PlayerLocation> location;
  };

  // Explicit reliable boundary, including same-space teleports and clearing.
  struct LocalLocation
  {
    std::optional<Domain::PlayerLocation> location;
  };

  struct LocalActorValues
  {
    Domain::ActorValueStorage actorValues;
  };

  struct CharacterStarted
  {
    Domain::CharacterName name;
  };

  struct CharacterRenamed
  {
    Domain::CharacterName name;
  };

  struct PlayerDetailsChanged
  {
    Domain::PlayerDetails details;
  };

  struct GameExited
  {};

  struct RequestSnapshot
  {};

  using ClientCommand = std::variant<
    SendChat,
    PostAnnouncement,
    PlaceGroundNote,
    ReportDeath,
    RemoveGroundMark,
    SetIdentityVisibility,
    LocalMovement,
    LocalLocation,
    LocalActorValues,
    CharacterStarted,
    CharacterRenamed,
    PlayerDetailsChanged,
    GameExited,
    RequestSnapshot>;

  struct QueuedClientCommand
  {
    std::uint64_t generation{};
    ClientCommand command;
  };

  enum class CommandPostResult
  {
    Queued,
    Replaced,
    Full,
    Closed
  };

  enum class SessionPhase
  {
    Disconnected,
    Connecting,
    Opening,
    Ready,
    Disconnecting,
    Faulted
  };

  enum class CommandFailureCode
  {
    StaleGeneration,
    SessionNotReady,
    Busy,
    InvalidRequest,
    EncodingFailed
  };

  struct CommandFailure
  {
    std::uint64_t      generation{};
    std::uint64_t      requestId{};
    CommandFailureCode code{};
  };

  using Credentials = Auth::Credentials;

  enum class AuthOperation
  {
    None,
    PasswordLogin,
    Resume,
    SignOut,
    ForgetSavedLogin,
    ResetPassword
  };

  struct ChatConfirmation
  {
    std::uint64_t         generation{};
    std::uint64_t         requestId{};
    Domain::ChatMessageId messageId{};
  };

  // Settles PlaceGroundNote, ReportDeath or RemoveGroundMark. The visible set
  // itself changes through the ordinary GroundMarksChanged delta.
  struct GroundMarkConfirmation
  {
    std::uint64_t        generation{};
    std::uint64_t        requestId{};
    Domain::GroundMarkId markId{};
    // The author's oldest mark of the same kind that gave way to this one.
    std::optional<Domain::GroundMarkId> evictedId;
    bool                                removed{};
  };

  // Settles SetIdentityVisibility: where the names are hidden now and the
  // pseudonym the others see there.
  struct IdentityConfirmation
  {
    std::uint64_t              generation{};
    std::uint64_t              requestId{};
    std::optional<std::string> pseudonym;
    Domain::HiddenIdentity     hiding{Domain::HiddenIdentity::None};
  };

  struct ClientStatus
  {
    SessionPhase      phase{SessionPhase::Disconnected};
    bool              authenticating{};
    bool              stopped{};
    std::string       error;
    std::string       serverName;
    Auth::FailureCode authFailure{};
    AuthOperation     authOperation{};
    bool              savedLogin{};
    std::string       savedUsername;
    std::uint32_t     authSequence{};  // Bumped per completion so an identical repeat is still observable.
    // The server pseudonym the others see for this session and where; absent
    // while the names are shown or outside a session.
    std::optional<std::string> pseudonym;
    Domain::HiddenIdentity     hiding{Domain::HiddenIdentity::None};
  };

  struct PasswordLogin
  {
    static constexpr auto      Operation = AuthOperation::PasswordLogin;
    Credentials                credentials;
    std::optional<std::string> registerName;
    bool                       remember{};
  };

  struct ResumeLogin
  {
    static constexpr auto Operation = AuthOperation::Resume;
  };

  struct SignOutAccount
  {
    static constexpr auto Operation = AuthOperation::SignOut;
  };

  struct ForgetLogin
  {
    static constexpr auto Operation = AuthOperation::ForgetSavedLogin;
  };

  struct ResetAccountPassword
  {
    static constexpr auto Operation = AuthOperation::ResetPassword;
    std::string           code;
    std::string           password;
  };

  using AuthenticationRequest = std::variant<PasswordLogin, ResumeLogin, SignOutAccount, ForgetLogin, ResetAccountPassword>;

  struct ClientControl
  {
    std::optional<AuthenticationRequest> authentication;
    bool                                 disconnect{};
  };

  struct ClientOutput
  {
    StateUpdateBatch state;
    ClientStatus     status;
    // Not reconstructible from a snapshot. Original generation is retained.
    std::vector<ServerRejectionEvent>   rejections;
    std::vector<CommandFailure>         commandFailures;
    std::vector<ChatConfirmation>       chatConfirmations;
    std::vector<GroundMarkConfirmation> groundMarkConfirmations;
    std::vector<IdentityConfirmation>   identityConfirmations;
  };

  // One network owner and one application main thread (also the UI consumer).
  // The sole synchronization boundary. The host owns the pump
  // and lifetime: this boundary creates no threads, transport or callbacks.
  class ClientExchange final
  {
public:

    using Ptr = std::unique_ptr<ClientExchange>;

    static Domain::Result<Ptr> TryCreate(std::size_t commandCapacity, std::size_t stateCapacity)
    {
      if (commandCapacity == 0)
        return std::unexpected{
            Domain::Error{Domain::ErrorCode::InvalidConfig, "commandCapacity"}
        };

      auto state = StateUpdateQueue::TryCreate(stateCapacity);
      if (!state) return std::unexpected{state.error()};

      return Ptr{
          new ClientExchange{commandCapacity, std::move(*state)}
      };
    }

    ClientExchange(const ClientExchange&)            = delete;
    ClientExchange& operator=(const ClientExchange&) = delete;

    // Main thread. Lifecycle has a reserved slot, independent of game-command
    // capacity and reply backpressure. Admission does not mean authentication.
    std::expected<void, std::string> PostLogin(
      Credentials                credentials,
      std::optional<std::string> registerName = std::nullopt,
      bool                       remember     = false)
    {
      return PostAuthentication(PasswordLogin{std::move(credentials), std::move(registerName), remember});
    }

    std::expected<void, std::string> PostAuthentication(AuthenticationRequest request)
    {
      std::lock_guard lock{mutex};
      if (inputClosed) return std::unexpected{"Client input is closed"};
      const auto operation     = std::visit([](const auto& value) { return value.Operation; }, request);
      const bool activeAllowed = operation == AuthOperation::SignOut || operation == AuthOperation::ForgetSavedLogin;
      if (
        status.authenticating || disconnectRequested ||
        (!activeAllowed && status.phase != SessionPhase::Disconnected && status.phase != SessionPhase::Faulted))
        return std::unexpected{"A connection operation or session is already active"};

      authenticationCanceled = false;
      status.authenticating  = true;
      status.authOperation   = operation;
      status.authFailure     = Auth::FailureCode::None;
      status.error.clear();
      pendingAuthentication.emplace(std::move(request));
      wake.notify_one();
      return {};
    }

    // Main thread. Cancellation remains visible while HTTP runs on the owner.
    void RequestDisconnect()
    {
      std::lock_guard lock{mutex};
      if (stopRequested || status.stopped) return;
      authenticationCanceled = true;
      disconnectRequested    = true;
      wake.notify_one();
    }

    void RequestStop()
    {
      std::lock_guard lock{mutex};
      inputClosed            = true;
      stopRequested          = true;
      authenticationCanceled = true;
      pendingAuthentication.reset();
      wake.notify_one();
    }

    ClientStatus Status() const
    {
      std::lock_guard lock{mutex};
      return status;
    }

    // Network owner only.
    ClientControl TakeControl()
    {
      std::lock_guard lock{mutex};
      ClientControl   result{std::move(pendingAuthentication), std::exchange(disconnectRequested, false)};
      pendingAuthentication.reset();
      return result;
    }

    bool AuthenticationCanceled() const
    {
      std::lock_guard lock{mutex};
      return authenticationCanceled;
    }

    bool StopRequested() const
    {
      std::lock_guard lock{mutex};
      return stopRequested;
    }

    void CompleteAuthentication(std::string error = {}, Auth::FailureCode failure = Auth::FailureCode::None)
    {
      std::lock_guard lock{mutex};
      status.authenticating = false;
      ++status.authSequence;
      if (authenticationCanceled)
        status.authFailure = Auth::FailureCode::Canceled;
      else
      {
        status.authFailure = failure;
        if (!error.empty()) status.error = std::move(error);
      }
    }

    void PublishSavedLogin(bool available, std::string username = {})
    {
      std::lock_guard lock{mutex};
      status.savedLogin    = available;
      status.savedUsername = std::move(username);
    }

    void PublishError(std::string error)
    {
      std::lock_guard lock{mutex};
      status.error = std::move(error);
    }

    void WaitForControl()
    {
      std::unique_lock lock{mutex};
      wake.wait_for(lock, std::chrono::milliseconds{10}, [&] {
        return stopRequested || pendingAuthentication.has_value() || disconnectRequested;
      });
    }

    // Main thread. Read by the owner when it opens the next session; a running
    // session changes only through SetIdentityVisibility.
    void SetHideIdentity(Domain::HiddenIdentity hiding)
    {
      std::lock_guard lock{mutex};
      hideIdentity = hiding;
    }

    Domain::HiddenIdentity HideIdentity() const
    {
      std::lock_guard lock{mutex};
      return hideIdentity;
    }

    // Owner only: the pseudonym of the current session and where it is shown,
    // as the server reported them.
    void PublishIdentity(std::optional<std::string> pseudonym, Domain::HiddenIdentity hiding)
    {
      std::lock_guard lock{mutex};
      status.pseudonym = std::move(pseudonym);
      status.hiding    = hiding;
    }

    // Shared by the network owner and its UI producer. Never reset on reconnect.
    std::optional<std::uint64_t> NextRequestId()
    {
      std::lock_guard lock{mutex};
      if (nextRequestId == 0) return std::nullopt;

      return nextRequestId++;
    }

    // Consumer side. Admission is not server acceptance; no model mutation.
    CommandPostResult Post(QueuedClientCommand command)
    {
      std::lock_guard lock{mutex};
      if (inputClosed) return CommandPostResult::Closed;

      // Capture before queueing/coalescing, never at encode time. An adapter may
      // supply its earlier sampling time using the same monotonic convention.
      if (
        auto* movement = std::get_if<LocalMovement>(&command.command);
        movement && movement->location && movement->location->sampledAtUs == 0)
      {
        movement->location->sampledAtUs = static_cast<std::uint64_t>(
          std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now().time_since_epoch()).count());
      }

      if (std::holds_alternative<LocalMovement>(command.command) && !commands.empty())
      {
        auto&       last     = commands.back();
        const auto* previous = std::get_if<LocalMovement>(&last.command);
        const auto& next     = std::get<LocalMovement>(command.command);
        const bool  sameContext =
          previous &&
          ((!previous->location && !next.location) ||
           (previous->location && next.location && previous->location->location.locationId == next.location->location.locationId));
        if (last.generation == command.generation && sameContext)
        {
          last = std::move(command);
          return CommandPostResult::Replaced;
        }
      }

      if (commands.size() >= maxCommands) return CommandPostResult::Full;

      commands.push_back(std::move(command));
      return CommandPostResult::Queued;
    }

    // Owner side, nonblocking so the network pump can continue polling ENet.
    // False means input is closed and all admitted commands have been taken.
    bool TakeCommands(std::vector<QueuedClientCommand>& output, std::size_t pendingReplies = 0)
    {
      output.clear();

      std::lock_guard lock{mutex};
      // Every taken command may fail locally or produce a server rejection.
      // Existing requests retain their result slots until a reply arrives.
      const auto free      = maxCommands - Settled();
      const auto available = pendingReplies >= free ? 0 : free - pendingReplies;
      auto       count     = std::min(commands.size(), available);
      if (count == commands.size())
        commands.swap(output);
      else
      {
        output.insert(output.end(), std::make_move_iterator(commands.begin()), std::make_move_iterator(commands.begin() + count));
        commands.erase(commands.begin(), commands.begin() + count);
      }

      return !inputClosed || !commands.empty() || !output.empty();
    }

    // Owner only, before starting a request outside TakeCommands (OpenSession).
    // The owner includes that request in pendingReplies until it settles.
    bool CanAcceptReplies(std::size_t count = 1)
    {
      std::lock_guard lock{mutex};
      return count <= maxCommands - Settled();
    }

    // Owner only, once per command obtained in the latest TakeCommands batch.
    bool PublishCommandFailure(CommandFailure failure)
    {
      std::lock_guard lock{mutex};
      if (Settled() >= maxCommands) return false;

      pendingFailures.push_back(failure);
      return true;
    }

    // Owner only, after decoded events. Exactly one exchange drains a model.
    // A requested snapshot consumes the pending changes that it includes.
    // False preserves model rejections for retry after Drain. State and phase
    // still publish so terminal failure can clear the UI. Only this owner adds
    // results; a concurrent Drain can only free room between check and insertion.
    [[nodiscard]] bool Publish(
      ClientModel&                          model,
      bool                                  requestSnapshot  = false,
      std::optional<SessionPhase>           nextPhase        = std::nullopt,
      std::string_view                      serverName       = {},
      std::optional<ChatConfirmation>       confirmation     = std::nullopt,
      std::optional<GroundMarkConfirmation> markConfirmation = std::nullopt,
      std::optional<IdentityConfirmation>   identity         = std::nullopt)
    {
      const bool accepted =
        CanAcceptReplies(model.PendingServerRejectionCount() + (confirmation ? 1 : 0) + (markConfirmation ? 1 : 0) + (identity ? 1 : 0));
      auto                             rejections = accepted ? model.TakeServerRejections() : std::vector<ServerRejectionEvent>{};
      std::optional<ClientStateUpdate> update;

      if (requestSnapshot || needsInitialSnapshot)
      {
        model.TakeChanges(scratch);
        update               = model.Snapshot();
        needsInitialSnapshot = false;
      }
      else
        update = TakeStateUpdate(model, scratch);

      std::lock_guard lock{mutex};
      if (nextPhase) status.phase = *nextPhase;
      status.serverName = serverName;
      if (accepted && confirmation) pendingConfirmations.push_back(*confirmation);
      if (accepted && markConfirmation) pendingMarkConfirmations.push_back(*markConfirmation);
      if (accepted && identity) pendingIdentityConfirmations.push_back(std::move(*identity));
      if (update && state->Publish(std::move(*update)) == StatePublishResult::SnapshotRequired) state->Publish(model.Snapshot());

      pendingRejections.insert(
        pendingRejections.end(),
        std::make_move_iterator(rejections.begin()),
        std::make_move_iterator(rejections.end()));
      return accepted;
    }

    // Owner only; consumers observe phase through the same synchronized exchange.
    void PublishPhase(SessionPhase value)
    {
      std::lock_guard lock{mutex};
      status.phase = value;
    }

    // Consumer side: drain once per frame, then route locally to UI/presence.
    void Drain(ClientOutput& output)
    {
      output.rejections.clear();
      output.commandFailures.clear();
      output.chatConfirmations.clear();
      output.groundMarkConfirmations.clear();
      output.identityConfirmations.clear();

      std::lock_guard lock{mutex};
      state->TakeAll(output.state);
      pendingRejections.swap(output.rejections);
      pendingFailures.swap(output.commandFailures);
      pendingConfirmations.swap(output.chatConfirmations);
      pendingMarkConfirmations.swap(output.groundMarkConfirmations);
      pendingIdentityConfirmations.swap(output.identityConfirmations);
      output.status = status;
    }

    // Either side. Accepted commands remain available for the owner to handle.
    void CloseInput()
    {
      std::lock_guard lock{mutex};
      inputClosed = true;
    }

    // Owner calls after publishing final state. Stopped cancels any commands
    // still waiting behind result backpressure; it does not fabricate replies.
    // No further owner operations after Finish. Host joins before destruction.
    void Finish()
    {
      std::lock_guard lock{mutex};
      inputClosed            = true;
      stopRequested          = true;
      authenticationCanceled = true;
      pendingAuthentication.reset();
      commands.clear();
      status.authenticating = false;
      status.stopped        = true;
      wake.notify_one();
    }

private:

    ClientExchange(std::size_t capacity, StateUpdateQueue::Ptr queue) : maxCommands{capacity}, state{std::move(queue)} {}

    // Results waiting for Drain; every kind shares the one command budget.
    std::size_t Settled() const noexcept
    {
      return pendingFailures.size() + pendingRejections.size() + pendingConfirmations.size() + pendingMarkConfirmations.size() +
             pendingIdentityConfirmations.size();
    }

    mutable std::mutex                   mutex;
    std::condition_variable              wake;
    const std::size_t                    maxCommands;
    std::vector<QueuedClientCommand>     commands;
    bool                                 inputClosed{};
    std::uint64_t                        nextRequestId{1};
    std::vector<CommandFailure>          pendingFailures;
    std::vector<ChatConfirmation>        pendingConfirmations;
    std::vector<GroundMarkConfirmation>  pendingMarkConfirmations;
    std::vector<IdentityConfirmation>    pendingIdentityConfirmations;
    ClientStatus                         status;
    Domain::HiddenIdentity               hideIdentity{Domain::HiddenIdentity::None};
    std::optional<AuthenticationRequest> pendingAuthentication;
    bool                                 disconnectRequested{};
    bool                                 authenticationCanceled{};
    bool                                 stopRequested{};
    StateUpdateQueue::Ptr                state;
    std::vector<ServerRejectionEvent>    pendingRejections;
    ChangeBatch                          scratch;                     // Owner only.
    bool                                 needsInitialSnapshot{true};  // Owner only.
  };

}
