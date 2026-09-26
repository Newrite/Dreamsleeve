export module Dreamsleeve.Client.ChatCache;

import std;

export import Dreamsleeve.Client.Domain.Logic;

export namespace Dreamsleeve::Client
{

  using namespace Domain;

  struct ChatMergeResult
  {
    // New distinct IDs accepted, including messages immediately evicted below.
    std::size_t added{};
    // Repeated messages equal exactly as received, in the batch or in the cache.
    std::size_t duplicates{};
    // Messages discarded from the combined cache and batch to enforce capacity.
    std::size_t evicted{};
  };

  struct ChatHistoryState
  {
    std::uint64_t                round{};
    std::optional<ChatMessageId> cursor{};
    bool                         hasMore{};
    // Sticky within one BeginHistory round; supplied by the server, not inferred.
    bool hasGap{};
  };

  struct ChatHistoryPage
  {
    ChatChannelId channelId{};
    // Local round token captured when issuing this page's history request.
    std::uint64_t round{};
    // The cursor used in the request producing this page.
    std::optional<ChatMessageId> after{};
    std::optional<ChatMessageId> nextCursor{};
    std::vector<ChatMessage>     messages{};
    bool                         hasMore{};
    bool                         hasGap{};
  };

  struct ChatCacheSnapshot
  {
    ChatChannelId channelId{};
    std::size_t   capacity{};
    // Ascending MessageId, independently of timestamps and arrival order.
    std::vector<ChatMessage> messages{};
    ChatHistoryState         history{};
    // Separate from the history cursor and the currently retained messages.
    std::optional<ChatMessageId> maxObservedId{};
  };

  // One serial owner must perform every operation, including reads and snapshots.
  // Transfer the returned owning values to UI/game threads through a synchronized
  // queue (or another explicit handoff); this class does not synchronize access.
  class ChatCache final
  {
public:

    ChatCache(const ChatCache&)                = delete;
    ChatCache& operator=(const ChatCache&)     = delete;
    ChatCache(ChatCache&&) noexcept            = default;
    ChatCache& operator=(ChatCache&&) noexcept = default;
    ~ChatCache()                               = default;

    static Domain::Result<ChatCache> TryCreate(ChatChannelId channelId, std::size_t capacity)
    {
      if (capacity == 0)
      {
        return std::unexpected{
            Domain::Error{Domain::ErrorCode::InvalidConfig, "capacity"}
        };
      }

      return ChatCache{channelId, capacity};
    }

    ChatChannelId ChannelId() const noexcept
    {
      return channelId;
    }

    std::size_t Capacity() const noexcept
    {
      return capacity;
    }

    std::size_t Count() const noexcept
    {
      return messages.size();
    }

    ChatHistoryState HistoryState() const noexcept
    {
      return history;
    }

    std::optional<ChatMessageId> MaxObservedId() const noexcept
    {
      return maxObservedId;
    }

    std::optional<ChatMessage> Find(ChatMessageId messageId) const
    {
      const auto found = messages.find(messageId);
      if (found == messages.end()) return std::nullopt;
      return found->second;
    }

    ChatCacheSnapshot Snapshot() const
    {
      ChatCacheSnapshot result{.channelId = channelId, .capacity = capacity, .history = history, .maxObservedId = maxObservedId};

      result.messages.reserve(messages.size());
      for (const auto& [id, message] : messages)
      {
        result.messages.push_back(message);
      }

      return result;
    }

    Domain::Result<ChatMergeResult> Merge(const ChatMessage& message)
    {
      return Merge(std::span<const ChatMessage>{&message, 1});
    }

    // Stage the whole batch before touching live state; preserve server values.
    // Duplicate/conflict checks cover retained messages and this whole batch.
    // No unbounded ledger is kept for IDs already evicted from the cache.
    // Consistency failures and allocation failures while staging leave the cache
    // unchanged. Commit transfers preallocated map nodes, then erases old IDs;
    // it does not allocate or copy the existing cache. std::bad_alloc propagates
    // as an exception rather than being reported as a domain error.
    Domain::Result<ChatMergeResult> Merge(std::span<const ChatMessage> batch)
    {
      std::map<ChatMessageId, ChatMessage> staged;
      ChatMergeResult                      result{};

      for (const auto& message : batch)
      {
        if (message.channelId != channelId)
        {
          return std::unexpected{
              Domain::Error{Domain::ErrorCode::ChannelMismatch, "channelId"}
          };
        }

        if (const auto found = messages.find(message.messageId); found != messages.end())
        {
          if (found->second != message)
          {
            return std::unexpected{
                Domain::Error{Domain::ErrorCode::ConflictingMessage, "messageId"}
            };
          }

          ++result.duplicates;
          continue;
        }

        if (const auto found = staged.find(message.messageId); found != staged.end())
        {
          if (found->second != message)
          {
            return std::unexpected{
                Domain::Error{Domain::ErrorCode::ConflictingMessage, "messageId"}
            };
          }

          ++result.duplicates;
          continue;
        }

        staged.emplace(message.messageId, message);
      }

      result.added = staged.size();
      if (!staged.empty())
      {
        const auto greatest = staged.rbegin()->first;
        if (!maxObservedId || greatest > *maxObservedId)
        {
          maxObservedId = greatest;
        }

        messages.merge(staged);
      }

      while (messages.size() > capacity)
      {
        messages.erase(messages.begin());
        ++result.evicted;
      }

      return result;
    }

    // Starts one ordered history walk. Live messages may arrive in between pages.
    // Begin again after a completed round, or when intentionally changing cursor.
    // Attach the returned round to requests and their matching replies. Do not
    // stamp an arriving response with the currently active round: an older round
    // may have been canceled and restarted at precisely the same cursor.
    Domain::Result<std::uint64_t> BeginHistory(std::optional<ChatMessageId> after = std::nullopt)
    {
      // Never reuse a token within this cache's lifetime, including after Clear.
      if (historyRound == std::numeric_limits<std::uint64_t>::max())
      {
        return std::unexpected{
            Domain::Error{Domain::ErrorCode::InvalidCursor, "history.round"}
        };
      }

      return BeginHistory(after, historyRound + 1);
    }

    // The enclosing model can allocate tokens across all of its caches. This
    // keeps replies distinguishable when a channel cache is removed/recreated.
    Domain::Result<std::uint64_t> BeginHistory(std::optional<ChatMessageId> after, std::uint64_t round)
    {
      if (after && *after == 0)
      {
        return std::unexpected{
            Domain::Error{Domain::ErrorCode::InvalidCursor, "after"}
        };
      }

      if (round == 0 || round <= historyRound)
      {
        return std::unexpected{
            Domain::Error{Domain::ErrorCode::InvalidCursor, "history.round"}
        };
      }

      historyRound   = round;
      history        = ChatHistoryState{.round = round, .cursor = after};
      historyPending = true;
      return history.round;
    }

    Domain::Result<ChatMergeResult> ApplyHistoryPage(const ChatHistoryPage& page)
    {
      if (page.channelId != channelId)
      {
        return std::unexpected{
            Domain::Error{Domain::ErrorCode::ChannelMismatch, "channelId"}
        };
      }

      if (page.round == 0 || page.round != history.round)
      {
        return std::unexpected{
            Domain::Error{Domain::ErrorCode::InvalidCursor, "history.round"}
        };
      }

      if (!historyPending || page.after != history.cursor)
      {
        return std::unexpected{
            Domain::Error{Domain::ErrorCode::InvalidCursor, "after"}
        };
      }

      auto expectedCursor = page.after;
      for (const auto& message : page.messages)
      {
        if (message.messageId == 0 || (page.after && message.messageId <= *page.after))
        {
          return std::unexpected{
              Domain::Error{Domain::ErrorCode::InvalidCursor, "messages.messageId"}
          };
        }

        if (!expectedCursor || message.messageId > *expectedCursor)
        {
          expectedCursor = message.messageId;
        }
      }

      if (page.nextCursor != expectedCursor || (page.hasMore && page.messages.empty()))
      {
        return std::unexpected{
            Domain::Error{Domain::ErrorCode::InvalidCursor, "nextCursor"}
        };
      }

      auto merged = Merge(std::span<const ChatMessage>{page.messages});
      if (!merged) return std::unexpected{std::move(merged.error())};

      // A valid old page still advances the cursor if capacity immediately
      // evicted every message in it. A live Merge never changes these fields.
      history.cursor  = page.nextCursor;
      history.hasMore = page.hasMore;
      history.hasGap  = history.hasGap || page.hasGap;
      historyPending  = page.hasMore;
      return merged;
    }

    // Stop the pending walk without discarding retained messages or high-water ID.
    // Delayed replies fail; the next BeginHistory must use a newer round token.
    void CancelHistory() noexcept
    {
      history        = {};
      historyPending = false;
    }

    // Call when discarding this server session or intentionally resetting chat.
    void Clear() noexcept
    {
      messages.clear();
      CancelHistory();
      maxObservedId.reset();
    }

private:

    explicit ChatCache(ChatChannelId channelId, std::size_t capacity) : channelId(channelId), capacity(capacity) {}

    ChatChannelId                        channelId{};
    std::size_t                          capacity{};
    std::map<ChatMessageId, ChatMessage> messages{};
    ChatHistoryState                     history{};
    std::optional<ChatMessageId>         maxObservedId{};
    bool                                 historyPending{};
    // Deliberately retained by Clear so delayed replies cannot match a new round.
    std::uint64_t historyRound{};
  };

}
