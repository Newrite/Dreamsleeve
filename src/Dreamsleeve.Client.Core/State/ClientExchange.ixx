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
  struct LocalPlayerState
  {
    std::optional<Domain::PlayerLocation> location;
    Domain::ActorValueStorage             actorValues;
  };

  struct CharacterStarted
  {
    Domain::CharacterName name;
  };

  struct GameExited
  {};

  struct RequestSnapshot
  {};

  using ClientCommand = std::variant<SendChat, LocalPlayerState, CharacterStarted, GameExited, RequestSnapshot>;

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

  struct ClientOutput
  {
    StateUpdateBatch state;
    // Not reconstructible from a snapshot. Original generation is retained.
    std::vector<ServerRejectionEvent> rejections;
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

    // Consumer side. Admission is not server acceptance; no model mutation.
    CommandPostResult Post(QueuedClientCommand command)
    {
      std::lock_guard lock{mutex};
      if (inputClosed) return CommandPostResult::Closed;
      if (std::holds_alternative<LocalPlayerState>(command.command) && !commands.empty())
      {
        auto& last = commands.back();
        if (last.generation == command.generation && std::holds_alternative<LocalPlayerState>(last.command))
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
    bool TakeCommands(std::vector<QueuedClientCommand>& output)
    {
      output.clear();
      std::lock_guard lock{mutex};
      commands.swap(output);
      return !inputClosed || !output.empty();
    }

    // Owner only, after decoded events. Exactly one exchange drains a model.
    // A requested snapshot consumes the pending changes that it includes.
    void Publish(ClientModel& model, bool requestSnapshot = false)
    {
      auto                             rejections = model.TakeServerRejections();
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
      if (update && state->Publish(std::move(*update)) == StatePublishResult::SnapshotRequired) state->Publish(model.Snapshot());
      pendingRejections.insert(
        pendingRejections.end(),
        std::make_move_iterator(rejections.begin()),
        std::make_move_iterator(rejections.end()));
    }

    // Consumer side: drain once per frame, then route locally to UI/presence.
    void Drain(ClientOutput& output)
    {
      output.rejections.clear();
      std::lock_guard lock{mutex};
      state->TakeAll(output.state);
      pendingRejections.swap(output.rejections);
      output.stopped = stopped;
    }

    // Either side. Accepted commands remain available for the owner to handle.
    void CloseInput()
    {
      std::lock_guard lock{mutex};
      inputClosed = true;
    }

    // Owner calls after handling admitted commands and publishing final state.
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
    bool                              stopped{};
    StateUpdateQueue::Ptr             state;
    std::vector<ServerRejectionEvent> pendingRejections;
    ChangeBatch                       scratch;                     // Owner only.
    bool                              needsInitialSnapshot{true};  // Owner only.
  };

}
