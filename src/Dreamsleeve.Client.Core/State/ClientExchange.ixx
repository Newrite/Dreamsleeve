export module Dreamsleeve.Client.Exchange;

import std;
export import Dreamsleeve.Client.Auth;
export import Dreamsleeve.Client.StateUpdateQueue;
export import Dreamsleeve.Client.GuildBook;

export namespace Dreamsleeve::Client
{

  struct SendChat
  {
    std::uint64_t         requestId{};
    Domain::ChatChannelId channelId{};
    std::string           text;
  };

  // Published into a system channel; settled like SendChat.
  struct PostAnnouncement
  {
    std::uint64_t                    requestId{};
    Domain::ChatChannelId            channelId{};
    std::string                      text;
    Domain::AnnouncementKind         kind{Domain::AnnouncementKind::Announcement};
    Domain::ClientAnnouncementSource source{Domain::ClientAnnouncementSource::ThirdParty};
    std::string                      signature;  // Required for ThirdParty.
  };

  // A note where the player stands.
  struct PlaceGroundNote
  {
    std::uint64_t               requestId{};
    std::string                 text;
    Domain::GroundMarkPlacement placement;
    Domain::GameDate            gameDate;  // Required: the calendar where the player stands now.
  };

  // The place the character died, with the killer's name or a cause; the
  // label may be empty. The client decides when a death is one.
  struct ReportDeath
  {
    std::uint64_t               requestId{};
    std::string                 label;
    Domain::GroundMarkPlacement placement;
    Domain::GameDate            gameDate;  // Required: the calendar at the death.
  };

  // The author's own mark; a moderator's, any mark.
  struct RemoveGroundMark
  {
    std::uint64_t        requestId{};
    Domain::GroundMarkId markId{};
  };

  // Hide or show this player's names for the others; the server picks the pseudonym.
  struct SetIdentityVisibility
  {
    std::uint64_t          requestId{};
    Domain::HiddenIdentity hiding{Domain::HiddenIdentity::None};
  };

  // A new display name for this player's own account; the username and
  // PlayerId never change. The server applies its word list and how often the
  // name may change; the own profile itself changes through a presence update.
  struct ChangeDisplayName
  {
    std::uint64_t requestId{};
    std::string   displayName;
  };

  // A new color of this player's name in chat, 0xRRGGBB. The server refuses a
  // color too dark to read and limits how often; others see it through presence.
  struct SetNameColor
  {
    std::uint64_t requestId{};
    std::uint32_t nameColor{};
  };

  // Moderator requests. The server checks the role and whom a moderator may
  // act on; a hidden player is named by the public PlayerId.

  // A mute or a ban for the minutes given, or until lifted without them. The
  // short reason is required; devices: a ban also covers the player's devices.
  struct SanctionPlayer
  {
    std::uint64_t                requestId{};
    Domain::PlayerId             playerId{};
    Domain::SanctionKind         kind{Domain::SanctionKind::Mute};
    std::optional<std::uint32_t> minutes;
    std::string                  reason;
    bool                         devices{};
  };

  struct LiftSanction
  {
    std::uint64_t        requestId{};
    Domain::PlayerId     playerId{};
    Domain::SanctionKind kind{Domain::SanctionKind::Mute};
  };

  // Ends the player's session now; they may sign in again at once.
  struct KickPlayer
  {
    std::uint64_t    requestId{};
    Domain::PlayerId playerId{};
    std::string      reason;
  };

  // Every sanction in force, offline players included, newest first.
  struct ListSanctions
  {
    std::uint64_t requestId{};
  };

  // Every mark of one player, far ones included, newest first.
  struct ListPlayerMarks
  {
    std::uint64_t    requestId{};
    Domain::PlayerId playerId{};
  };

  // The player's notes, death marks or both; at least one kind.
  struct ClearPlayerMarks
  {
    std::uint64_t    requestId{};
    Domain::PlayerId playerId{};
    bool             notes{};
    bool             deaths{};
  };

  // One message of a channel, gone for everyone.
  struct DeleteChatMessage
  {
    std::uint64_t         requestId{};
    Domain::ChatChannelId channelId{};
    Domain::ChatMessageId messageId{};
  };

  // Guild requests. The server judges the roles, the name rules and the limits;
  // the effect arrives with the guild book.
  struct CreateGuild
  {
    std::string name;
  };

  // An online player, who answers within the server's invitation lifetime.
  struct InviteToGuild
  {
    Domain::GuildId  guildId{};
    Domain::PlayerId playerId{};
  };

  struct AnswerGuildInvite
  {
    Domain::GuildId guildId{};
    bool            accept{};
  };

  // Not the master, who transfers the guild or disbands it first.
  struct LeaveGuild
  {
    Domain::GuildId guildId{};
  };

  struct ExcludeGuildMember
  {
    Domain::GuildId  guildId{};
    Domain::PlayerId playerId{};
  };

  // Member or officer; the master's role passes by TransferGuild.
  struct SetGuildRole
  {
    Domain::GuildId   guildId{};
    Domain::PlayerId  playerId{};
    Domain::GuildRole role{Domain::GuildRole::Member};
  };

  // The master becomes an officer.
  struct TransferGuild
  {
    Domain::GuildId  guildId{};
    Domain::PlayerId playerId{};
  };

  struct MuteGuildMember
  {
    Domain::GuildId              guildId{};
    Domain::PlayerId             playerId{};
    std::optional<std::uint32_t> minutes;  // Absent: until lifted.
    std::string                  reason;
  };

  struct UnmuteGuildMember
  {
    Domain::GuildId  guildId{};
    Domain::PlayerId playerId{};
  };

  struct DisbandGuild
  {
    Domain::GuildId guildId{};
  };

  using GuildAction = std::variant<
    CreateGuild,
    InviteToGuild,
    AnswerGuildInvite,
    LeaveGuild,
    ExcludeGuildMember,
    SetGuildRole,
    TransferGuild,
    MuteGuildMember,
    UnmuteGuildMember,
    DisbandGuild>;

  struct GuildRequest
  {
    std::uint64_t requestId{};
    GuildAction   action;
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
    ChangeDisplayName,
    SetNameColor,
    SanctionPlayer,
    LiftSanction,
    KickPlayer,
    ListSanctions,
    ListPlayerMarks,
    ClearPlayerMarks,
    DeleteChatMessage,
    GuildRequest,
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

  // No session and no transport of one: another session may start.
  constexpr bool SessionIdle(SessionPhase phase) noexcept
  {
    return phase == SessionPhase::Disconnected || phase == SessionPhase::Faulted;
  }

  enum class CommandFailureCode
  {
    StaleGeneration,
    SessionNotReady,
    Busy,
    InvalidRequest,
    EncodingFailed
  };

  using RequestRejectionCode = ::Protocol::Chat::RequestRejectionCode;

  // The server refused a request. The code decides; the message explains, and
  // an unknown nonzero code from a newer server keeps its message.
  struct ServerRejection
  {
    RequestRejectionCode code{};
    std::string          message;
    std::string          field;
  };

  // How the server settled a command. The visible state itself changes through
  // the ordinary deltas, so the author sees it the way everyone does.
  struct MessagePublished  // SendChat or PostAnnouncement
  {
    Domain::ChatMessageId messageId{};
  };

  struct MarkPlaced  // PlaceGroundNote or ReportDeath
  {
    Domain::GroundMarkId markId{};
    // The author's oldest mark of the same kind that gave way to this one.
    std::optional<Domain::GroundMarkId> evictedId;
  };

  struct MarkRemoved
  {
    Domain::GroundMarkId markId{};
  };

  // Where the names are hidden now; the pseudonym is in ClientStatus.
  struct IdentityChanged
  {
    Domain::HiddenIdentity hiding{Domain::HiddenIdentity::None};
  };

  // The display name as the server stored it.
  struct NameChanged
  {
    std::string displayName;
  };

  // The name color as the server stored it.
  struct ColorChanged
  {
    std::uint32_t nameColor{};
  };

  // Moderator answers. Players are named by PlayerId; the UI resolves names.
  struct Sanctioned
  {
    Domain::Sanction sanction;
  };

  struct Lifted
  {
    Domain::PlayerId     playerId{};
    Domain::SanctionKind kind{Domain::SanctionKind::Mute};
  };

  struct Kicked
  {
    Domain::PlayerId playerId{};
  };

  struct SanctionsListed
  {
    std::vector<Domain::Sanction> sanctions;
  };

  struct MarksListed
  {
    Domain::PlayerId                playerId{};
    std::vector<Domain::GroundMark> marks;
  };

  struct MarksCleared
  {
    Domain::PlayerId playerId{};
    std::uint32_t    removed{};
  };

  // The chat drops the message through the ordinary delta, as for everyone.
  struct MessageDeleted
  {
    Domain::ChatChannelId channelId{};
    Domain::ChatMessageId messageId{};
  };

  // A guild request was done; the guild book already shows its effect. A
  // created guild's new ID.
  struct GuildDone
  {
    Domain::GuildId guildId{};
  };

  // Exactly one result settles every command with a request ID: the server's
  // answer, its refusal or a local failure, with the command's generation.
  struct CommandResult
  {
    using Outcome = std::variant<
      MessagePublished,
      MarkPlaced,
      MarkRemoved,
      IdentityChanged,
      NameChanged,
      ColorChanged,
      Sanctioned,
      Lifted,
      Kicked,
      SanctionsListed,
      MarksListed,
      MarksCleared,
      MessageDeleted,
      GuildDone,
      ServerRejection,
      CommandFailureCode>;

    std::uint64_t generation{};
    std::uint64_t requestId{};
    Outcome       outcome;
  };

  using Credentials = Auth::Credentials;

  enum class AuthOperation
  {
    None,
    PasswordLogin,
    Resume,
    SignOut,
    ForgetSavedLogin,
    ResetPassword,
    SteamLogin
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
    // What the server offers: who may register, whether Steam sign-in is on.
    Auth::Methods methods;
    // The page of a Steam sign-in in progress, for "copy the link", and who
    // was asked to open it in the browser or why that failed; empty outside one.
    std::string steamPage;
    std::string steamBrowser;
    std::string steamBrowserError;
    // The server pseudonym the others see for this session and where; absent
    // while the names are shown or outside a session.
    std::optional<std::string> pseudonym;
    Domain::HiddenIdentity     hiding{Domain::HiddenIdentity::None};
    // The player's mute in this session, as the server reported it.
    std::optional<Domain::MuteState> mute;
    // The player's role in this session; a moderator gets the moderator tools.
    Domain::PlayerRole role{Domain::PlayerRole::Player};
    // The player's guilds and invitations in this session, published with the
    // chat state of their channels; absent until the server sent them. Every
    // change is a new book, so comparing pointers tells a change.
    std::shared_ptr<const GuildBook> guilds;
    // Why the last session ended or a sign-in was refused by a ban; kept until
    // the next sign-in. The sequence tells a repeat of the same notice apart.
    std::optional<Domain::SessionEnd> sessionEnd;
    std::uint32_t                     sessionEndSequence{};

    // Nothing runs: no sign-in in flight and no session.
    bool Idle() const noexcept
    {
      return !authenticating && SessionIdle(phase);
    }

    bool Ready() const noexcept
    {
      return phase == SessionPhase::Ready && !stopped;
    }
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

  // Sign-in in the browser through Steam; remember saves the login like a password sign-in.
  struct SteamLogin
  {
    static constexpr auto Operation = AuthOperation::SteamLogin;
    bool                  remember{true};
  };

  using AuthenticationRequest = std::variant<PasswordLogin, ResumeLogin, SignOutAccount, ForgetLogin, ResetAccountPassword, SteamLogin>;

  struct ClientControl
  {
    std::optional<AuthenticationRequest> authentication;
    bool                                 disconnect{};
  };

  struct ClientOutput
  {
    StateUpdateBatch state;
    ClientStatus     status;
    // Not reconstructible from a snapshot; in the order the owner settled them.
    std::vector<CommandResult> results;
  };

  // One network owner and one application main thread (also the UI consumer).
  // The sole synchronization boundary. The host owns the pump
  // and lifetime: this boundary creates no threads, transport or callbacks.
  class ClientExchange final
  {
public:

    using Ptr = std::unique_ptr<ClientExchange>;

    // Both queues hold at least one entry; the settings check asks here too.
    static std::optional<std::string_view> InvalidCapacity(std::size_t commandCapacity, std::size_t stateCapacity) noexcept
    {
      if (commandCapacity == 0) return "commandCapacity";
      if (stateCapacity == 0) return "stateCapacity";
      return std::nullopt;
    }

    static Domain::Result<Ptr> TryCreate(std::size_t commandCapacity, std::size_t stateCapacity)
    {
      if (const auto field = InvalidCapacity(commandCapacity, stateCapacity))
        return std::unexpected{
            Domain::Error{Domain::ErrorCode::InvalidConfig, std::string{*field}}
        };

      return Ptr{
          new ClientExchange{commandCapacity, StateUpdateQueue::Create(stateCapacity)}
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
      if (status.authenticating || disconnectRequested || (!activeAllowed && !SessionIdle(status.phase)))
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

    void PublishSteamPage(std::string page, std::string browser = {}, std::string browserError = {})
    {
      std::lock_guard lock{mutex};
      status.steamPage         = std::move(page);
      status.steamBrowser      = std::move(browser);
      status.steamBrowserError = std::move(browserError);
    }

    void PublishMethods(Auth::Methods methods)
    {
      std::lock_guard lock{mutex};
      status.methods = methods;
    }

    void PublishError(std::string error)
    {
      std::lock_guard lock{mutex};
      status.error = std::move(error);
    }

    void PublishMute(std::optional<Domain::MuteState> mute)
    {
      std::lock_guard lock{mutex};
      status.mute = std::move(mute);
    }

    void PublishRole(Domain::PlayerRole role)
    {
      std::lock_guard lock{mutex};
      status.role = role;
    }

    // A kick, a ban or a revocation from the server, or a ban refusing sign-in;
    // std::nullopt when the next sign-in starts.
    void PublishSessionEnd(std::optional<Domain::SessionEnd> end)
    {
      std::lock_guard lock{mutex};
      if (end) ++status.sessionEndSequence;
      status.sessionEnd = std::move(end);
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
        if (last.generation == command.generation && previous && Domain::Motion::SameContext(previous->location, next.location))
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

    // Owner only: a result that changes no state, such as a local failure of
    // a command from the latest TakeCommands batch.
    bool PublishResult(CommandResult result)
    {
      std::lock_guard lock{mutex};
      if (Settled() >= maxCommands) return false;

      pendingResults.push_back(std::move(result));
      return true;
    }

    // Owner only, after decoded events. Exactly one exchange drains a model.
    // A requested snapshot consumes the pending changes that it includes; a
    // result travels with the state it settles. False means the result did not
    // fit; state and phase still publish so terminal failure can clear the UI.
    // Only this owner adds results; a concurrent Drain can only free room
    // between check and insertion.
    [[nodiscard]] bool Publish(
      ClientModel&                     model,
      bool                             requestSnapshot = false,
      std::optional<SessionPhase>      nextPhase       = std::nullopt,
      std::string_view                 serverName      = {},
      std::optional<CommandResult>     result          = std::nullopt,
      std::shared_ptr<const GuildBook> guilds          = nullptr)
    {
      const bool                       accepted = !result || CanAcceptReplies();
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
      status.guilds     = std::move(guilds);
      if (accepted && result) pendingResults.push_back(std::move(*result));
      if (update && state->Publish(std::move(*update)) == StatePublishResult::SnapshotRequired) state->Publish(model.Snapshot());
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
      output.results.clear();

      std::lock_guard lock{mutex};
      state->TakeAll(output.state);
      pendingResults.swap(output.results);
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

    // Results waiting for Drain share the one command budget.
    std::size_t Settled() const noexcept
    {
      return pendingResults.size();
    }

    mutable std::mutex                   mutex;
    std::condition_variable              wake;
    const std::size_t                    maxCommands;
    std::vector<QueuedClientCommand>     commands;
    bool                                 inputClosed{};
    std::uint64_t                        nextRequestId{1};
    std::vector<CommandResult>           pendingResults;
    ClientStatus                         status;
    Domain::HiddenIdentity               hideIdentity{Domain::HiddenIdentity::None};
    std::optional<AuthenticationRequest> pendingAuthentication;
    bool                                 disconnectRequested{};
    bool                                 authenticationCanceled{};
    bool                                 stopRequested{};
    StateUpdateQueue::Ptr                state;
    ChangeBatch                          scratch;                     // Owner only.
    bool                                 needsInitialSnapshot{true};  // Owner only.
  };

}
