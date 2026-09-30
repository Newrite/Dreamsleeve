export module Dreamsleeve.Client.GroundMarkStore;

import std;

export import Dreamsleeve.Client.Domain.Logic;
import Dreamsleeve.Client.Changes;

export namespace Dreamsleeve::Client
{

  // A reliable delta of the marks the server shows this observer. Clear starts
  // a new baseline; viewRevision increases with every delta of one session.
  struct GroundMarksChanged
  {
    std::uint64_t                     viewRevision{};
    std::vector<Domain::GroundMark>   added;
    std::vector<Domain::GroundMarkId> removedIds;
    bool                              clear{};
  };

  // Every mark of this player wherever it stands, as the server last listed
  // them: a full replacement, independent of the visible set.
  struct OwnGroundMarksReplaced
  {
    std::vector<Domain::GroundMark> marks;
  };

  struct GroundMarkStoreSnapshot
  {
    std::uint64_t viewRevision{};
    // Ascending mark ID.
    std::vector<Domain::GroundMark> marks;
    // The player's own marks, ascending mark ID.
    std::vector<Domain::GroundMark> own;
  };

  // Marks visible from the player's position, as the server projects them. The
  // network thread owns it; snapshots and transitions are detached values.
  class GroundMarkStore final
  {
public:

    GroundMarkStore()                                  = default;
    GroundMarkStore(const GroundMarkStore&)            = delete;
    GroundMarkStore& operator=(const GroundMarkStore&) = delete;
    GroundMarkStore(GroundMarkStore&&)                 = default;
    GroundMarkStore& operator=(GroundMarkStore&&)      = default;

    // Applies one reliable delta. Revisions must increase; a repeated or older
    // one is a protocol fault, not a duplicate to skip. Removing an unknown
    // mark is harmless. The result is the exact visible transition, in order:
    // a clear, then removals, then additions.
    Domain::Result<std::vector<GroundMarkChange>> Apply(const GroundMarksChanged& update)
    {
      if (update.viewRevision == 0 || update.viewRevision <= viewRevision)
        return std::unexpected{
            Domain::Error{Domain::ErrorCode::InvalidCursor, "viewRevision"}
        };

      std::vector<GroundMarkChange> changes;
      if (update.clear && !marks.empty())
      {
        marks.clear();
        changes.emplace_back(GroundMarksCleared{});
      }
      else if (update.clear)
        changes.emplace_back(GroundMarksCleared{});

      GroundMarksRemoved removed;
      for (const auto id : update.removedIds)
        if (marks.erase(id) != 0) removed.markIds.push_back(id);
      if (!removed.markIds.empty()) changes.emplace_back(std::move(removed));

      GroundMarksAdded added;
      for (const auto& mark : update.added)
      {
        marks.insert_or_assign(mark.markId, mark);
        added.marks.push_back(mark);
      }
      if (!added.marks.empty()) changes.emplace_back(std::move(added));

      viewRevision = update.viewRevision;
      return changes;
    }

    // The server's complete list of the player's own marks replaces the previous one.
    void ReplaceOwn(std::vector<Domain::GroundMark> list)
    {
      std::ranges::sort(list, {}, &Domain::GroundMark::markId);
      own = std::move(list);
    }

    const std::vector<Domain::GroundMark>& Own() const noexcept
    {
      return own;
    }

    // A session boundary: nothing is visible and the next delta starts over.
    void Clear() noexcept
    {
      marks.clear();
      own.clear();
      viewRevision = 0;
    }

    GroundMarkStoreSnapshot Snapshot() const
    {
      GroundMarkStoreSnapshot result{.viewRevision = viewRevision};
      result.marks.reserve(marks.size());
      for (const auto& [id, mark] : marks)
        result.marks.push_back(mark);
      result.own = own;
      return result;
    }

private:

    std::map<Domain::GroundMarkId, Domain::GroundMark> marks;
    std::vector<Domain::GroundMark>                    own;
    std::uint64_t                                      viewRevision{};
  };

}
