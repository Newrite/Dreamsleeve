export module Dreamsleeve.Client.Changes;

import std;

export import Dreamsleeve.Client.Domain;

export namespace Dreamsleeve::Client
{

  // Owner-local invalidations, not replayable UI deltas or motion samples.
  // Successful idempotent operations may also mark an entry. Read its current
  // value on the model owner before applying more updates or crossing threads.
  struct ChangeBatch final
  {
    // Model cursor at TakeChanges(), including successful no-op operations.
    // This is not a contiguous sequence of UI notifications.
    std::uint64_t generation{};
    std::uint64_t revision{};

    // A session boundary or incomplete tracking supersedes individual IDs:
    // use Snapshot even if generation stayed the same.
    bool requiresSnapshot{};
    bool selfPlayerChanged{};
    // Reconcile the complete online list, including players now absent.
    bool playersReplaced{};

    // Unique touched IDs, including removals. A missing Find result means that
    // the entity is absent now; intermediate add/remove operations are coalesced.
    std::vector<Domain::PlayerId>      players;
    std::vector<Domain::ChatChannelId> chats;

    bool Empty() const noexcept
    {
      return !requiresSnapshot && !selfPlayerChanged && !playersReplaced && players.empty() && chats.empty();
    }

    // Keep allocated storage for the next owner iteration.
    void Clear() noexcept
    {
      generation        = 0;
      revision          = 0;
      requiresSnapshot  = false;
      selfPlayerChanged = false;
      playersReplaced   = false;
      players.clear();
      chats.clear();
    }
  };

}
