export module Dreamsleeve.Client.StateUpdate;

import std;

export import Dreamsleeve.Client.Model;

export namespace Dreamsleeve::Client
{

  struct ChatStateChange final
  {
    Domain::ChatChannelId channelId{};
    // Missing state removes the channel. A reset starts a new, empty cache
    // before applying this batch's content, even if the ID was already known.
    std::optional<ChatCacheState> state;
    bool                          resetContent{};
  };

  struct ClientStateDelta final
  {
    std::uint64_t                   generation{};
    std::uint64_t                   revision{};
    bool                            selfPlayerChanged{};
    std::optional<Domain::PlayerId> selfPlayerId;
    // Clear the recipient's online list before upserting players when true.
    bool                          playersReplaced{};
    std::vector<Domain::Player>   players;
    std::vector<Domain::PlayerId> removedPlayers;
    // Reconcile channel existence/resets before replaying ordered content.
    std::vector<ChatStateChange>   chats;
    std::vector<ChatContentChange> chatContent;
  };

  using ClientStateUpdate = std::variant<ClientSnapshot, ClientStateDelta>;

  // Owner-only: drain and resolve without interleaving another model operation.
  // The returned value owns its data; cross-thread delivery still needs a queue.
  // Rejections remain available through TakeServerRejections().
  // Allocation failures propagate; do not continue this session after one.
  std::optional<ClientStateUpdate> TakeStateUpdate(ClientModel& model, ChangeBatch& scratch)
  {
    model.TakeChanges(scratch);
    if (scratch.Empty()) return std::nullopt;
    if (scratch.requiresSnapshot) return ClientStateUpdate{model.Snapshot()};

    ClientStateDelta delta;
    delta.generation        = scratch.generation;
    delta.revision          = scratch.revision;
    delta.selfPlayerChanged = scratch.selfPlayerChanged;
    if (delta.selfPlayerChanged) delta.selfPlayerId = model.SelfPlayerId();

    delta.playersReplaced = scratch.playersReplaced;
    if (delta.playersReplaced)
    {
      delta.players = model.SnapshotPlayers();
    }
    else
    {
      for (const auto id : scratch.players)
      {
        if (auto player = model.FindPlayer(id))
          delta.players.push_back(std::move(*player));
        else
          delta.removedPlayers.push_back(id);
      }
    }

    for (const auto id : scratch.chats)
    {
      const auto state = model.FindChatState(id);
      const bool reset = state && std::ranges::find(scratch.resetChats, id) != scratch.resetChats.end();
      delta.chats.push_back(ChatStateChange{id, state, reset});
    }

    delta.chatContent = std::move(scratch.chatContent);

    return ClientStateUpdate{std::move(delta)};
  }

}
