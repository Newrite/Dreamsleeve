export module Dreamsleeve.Client.Exchange;

import std;
export import Dreamsleeve.Client.StateUpdateQueue;

export namespace Dreamsleeve::Client
{

  struct SendChat
  {
    std::uint64_t         requestId{};
    Domain::ChatChannelId channelId{};
    std::string           text;
  };

  // Complete sampled values, not a patch. Only adjacent pending samples from
  // the same session can replace one another; transitions remain ordered.
  struct LocalMovement
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

  using ClientCommand = std::variant<SendChat, LocalMovement, LocalActorValues, CharacterStarted, CharacterRenamed, PlayerDetailsChanged, GameExited, RequestSnapshot>;

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

  struct ClientOutput
  {
    StateUpdateBatch state;
    SessionPhase     phase{SessionPhase::Disconnected};
    // Not reconstructible from a snapshot. Original generation is retained.
    std::vector<ServerRejectionEvent> rejections;
    std::vector<CommandFailure>        commandFailures;
    bool                              stopped{};
  };

  // One network/model owner and one game/UI consumer. The host owns the pump
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
      if (auto* movement = std::get_if<LocalMovement>(&command.command);
          movement && movement->location && movement->location->sampledAtUs == 0)
      {
        movement->location->sampledAtUs = static_cast<std::uint64_t>(
          std::chrono::duration_cast<std::chrono::microseconds>(std::chrono::steady_clock::now().time_since_epoch()).count());
      }

      if (std::holds_alternative<LocalMovement>(command.command) && !commands.empty())
      {
        auto& last = commands.back();
        if (last.generation == command.generation && std::holds_alternative<LocalMovement>(last.command))
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
    bool TakeCommands(std::vector<QueuedClientCommand>& output, std::size_t pendingReplies = 0, std::size_t sampleBudget = std::numeric_limits<std::size_t>::max())
    {
      output.clear();

      std::lock_guard lock{mutex};
      // Every taken command may fail locally or produce a server rejection.
      // Existing requests retain their result slots until a reply arrives.
      const auto free = maxCommands - pendingFailures.size() - pendingRejections.size();
      const auto available = pendingReplies >= free ? 0 : free - pendingReplies;
      auto count = std::min(commands.size(), available);
      for (std::size_t index = 0; index < count; ++index)
      {
        if (!std::holds_alternative<LocalMovement>(commands[index].command)) continue;
        if (sampleBudget == 0)
        {
          count = index;
          break;
        }
        --sampleBudget;
      }
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
      return count <= maxCommands - pendingFailures.size() - pendingRejections.size();
    }

    // Owner only, once per command obtained in the latest TakeCommands batch.
    bool PublishCommandFailure(CommandFailure failure)
    {
      std::lock_guard lock{mutex};
      if (pendingFailures.size() + pendingRejections.size() >= maxCommands) return false;

      pendingFailures.push_back(failure);
      return true;
    }

    // Owner only, after decoded events. Exactly one exchange drains a model.
    // A requested snapshot consumes the pending changes that it includes.
    // False preserves model rejections for retry after Drain. State and phase
    // still publish so terminal failure can clear the UI. Only this owner adds
    // results; a concurrent Drain can only free room between check and insertion.
    [[nodiscard]] bool Publish(ClientModel& model, bool requestSnapshot = false, std::optional<SessionPhase> nextPhase = std::nullopt)
    {
      const bool accepted = CanAcceptReplies(model.PendingServerRejectionCount());
      auto rejections = accepted ? model.TakeServerRejections() : std::vector<ServerRejectionEvent>{};
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
      if (nextPhase) phase = *nextPhase;
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
      phase = value;
    }

    // Consumer side: drain once per frame, then route locally to UI/presence.
    void Drain(ClientOutput& output)
    {
      output.rejections.clear();
      output.commandFailures.clear();

      std::lock_guard lock{mutex};
      state->TakeAll(output.state);
      pendingRejections.swap(output.rejections);
      pendingFailures.swap(output.commandFailures);
      output.stopped = stopped;
      output.phase   = phase;
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
      inputClosed = true;
      stopped     = true;
    }

private:

    ClientExchange(std::size_t capacity, StateUpdateQueue::Ptr queue) : maxCommands{capacity}, state{std::move(queue)} {}

    std::mutex                        mutex;
    const std::size_t                 maxCommands;
    std::vector<QueuedClientCommand>  commands;
    bool                              inputClosed{};
    std::uint64_t                     nextRequestId{1};
    std::vector<CommandFailure>       pendingFailures;
    bool                              stopped{};
    SessionPhase                      phase{SessionPhase::Disconnected};
    StateUpdateQueue::Ptr             state;
    std::vector<ServerRejectionEvent> pendingRejections;
    ChangeBatch                       scratch;                     // Owner only.
    bool                              needsInitialSnapshot{true};  // Owner only.
  };

}
