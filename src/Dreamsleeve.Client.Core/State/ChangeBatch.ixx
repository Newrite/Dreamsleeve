export module Dreamsleeve.Client.Changes;

import std;

export import Dreamsleeve.Client.Domain;

export namespace Dreamsleeve::Client
{

  struct ChatMessagesAdded final
  {
    Domain::ChatChannelId            channelId{};
    std::vector<Domain::ChatMessage> messages{};
  };

  struct ChatMessagesRemoved final
  {
    Domain::ChatChannelId              channelId{};
    std::vector<Domain::ChatMessageId> messageIds{};
  };

  using ChatContentChange = std::variant<ChatMessagesRemoved, ChatMessagesAdded>;

  // Owner-local changes. Player IDs and chat metadata IDs are invalidations: resolve
  // their current value on the model owner before crossing threads. Chat content
  // is different: it is already an owning ordered delta and must be forwarded in
  // the order stored here.
  struct ChangeBatch final
  {
    // Model cursor at TakeChanges(), including successful no-op operations.
    // This is not a contiguous sequence of UI notifications.
    std::uint64_t generation{};
    std::uint64_t revision{};

    // A session boundary or incomplete tracking supersedes individual changes:
    // use Snapshot even if generation stayed the same.
    bool requiresSnapshot{};
    bool selfPlayerChanged{};
    // Reconcile the complete online list, including players now absent.
    bool playersReplaced{};

    // Unique touched IDs, including removals. A missing Find result means that
    // the entity is absent now; intermediate add/remove operations are coalesced.
    std::vector<Domain::PlayerId> players;

    // Chat existence/history metadata changed. Resolve with FindChatState;
    // ordinary incoming messages do not mark this list.
    std::vector<Domain::ChatChannelId> chats;

    // Exact visible cache transitions. Unlike invalidations, these are ordered:
    // applying them in sequence reproduces the native chat cache contents.
    std::vector<ChatContentChange> chatContent;

    bool Empty() const noexcept
    {
      return !requiresSnapshot && !selfPlayerChanged && !playersReplaced && players.empty() && chats.empty() && chatContent.empty();
    }

    // Keep allocated top-level storage for the next owner iteration. Nested
    // vectors moved into chatContent are released when this batch is consumed.
    void Clear() noexcept
    {
      generation        = 0;
      revision          = 0;
      requiresSnapshot  = false;
      selfPlayerChanged = false;
      playersReplaced   = false;
      players.clear();
      chats.clear();
      chatContent.clear();
    }
  };

}
