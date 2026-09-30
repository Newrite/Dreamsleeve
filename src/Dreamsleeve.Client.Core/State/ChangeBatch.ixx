export module Dreamsleeve.Client.Changes;

import std;

export import Dreamsleeve.Client.Domain;

export namespace Dreamsleeve::Client
{

  using MovementClock = std::chrono::steady_clock;

  // Ordered observations, independent of coalesced player invalidations.
  // An absent location is a visibility/lifecycle boundary and clears history.
  struct MovementObservation final
  {
    Domain::PlayerId                      playerId{};
    std::uint64_t                         characterGeneration{};
    MovementClock::time_point             receivedAt{};
    std::optional<Domain::PlayerLocation> location;
    std::uint64_t                         viewRevision{};
  };

  struct ChatMessagesAdded final
  {
    Domain::ChatChannelId            channelId{};
    std::vector<Domain::ChatMessage> messages{};
  };

  // Messages the cache let go to keep its capacity; the UI bounds its own history.
  struct ChatMessagesEvicted final
  {
    Domain::ChatChannelId              channelId{};
    std::vector<Domain::ChatMessageId> messageIds{};
  };

  // Messages a moderator removed: gone from the cache and from any history.
  struct ChatMessagesDeleted final
  {
    Domain::ChatChannelId              channelId{};
    std::vector<Domain::ChatMessageId> messageIds{};
  };

  using ChatContentChange = std::variant<ChatMessagesEvicted, ChatMessagesAdded, ChatMessagesDeleted>;

  // Exact transitions of the visible ground marks, in order: a clear starts a
  // new baseline, removals and additions follow.
  struct GroundMarksCleared final
  {};

  struct GroundMarksRemoved final
  {
    std::vector<Domain::GroundMarkId> markIds{};
  };

  struct GroundMarksAdded final
  {
    std::vector<Domain::GroundMark> marks{};
  };

  using GroundMarkChange = std::variant<GroundMarksCleared, GroundMarksRemoved, GroundMarksAdded>;

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

    // Subset of chats registered since the last drain. If still present,
    // clear the recipient's old contents before applying chatContent.
    std::vector<Domain::ChatChannelId> resetChats;

    // Exact visible cache transitions. Unlike invalidations, these are ordered:
    // applying them in sequence reproduces the native chat cache contents.
    std::vector<ChatContentChange>   chatContent;
    std::vector<MovementObservation> movement;
    // Ordered like chatContent: applying them in sequence reproduces the visible marks.
    std::vector<GroundMarkChange> groundMarks;
    // The player's own list was replaced; resolve the current value on the owner.
    bool ownGroundMarksReplaced{};

    bool Empty() const noexcept
    {
      return !requiresSnapshot && !selfPlayerChanged && !playersReplaced && players.empty() && chats.empty() && resetChats.empty() &&
             chatContent.empty() && movement.empty() && groundMarks.empty() && !ownGroundMarksReplaced;
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
      resetChats.clear();
      chatContent.clear();
      movement.clear();
      groundMarks.clear();
      ownGroundMarksReplaced = false;
    }
  };

}
