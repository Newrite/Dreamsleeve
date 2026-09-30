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

    // Exact visible transition from the cache state before Merge to the state
    // after Merge. Newly accepted messages that are immediately evicted do not
    // appear here because a consumer never needs to render them.
    std::vector<ChatMessage>   addedMessages{};
    std::vector<ChatMessageId> removedMessageIds{};
  };

  struct ChatCacheState
  {
    ChatChannelId           channelId{};
    Domain::ChatChannelKind kind{Domain::ChatChannelKind::Global};
    std::size_t             capacity{};
    std::size_t             count{};
  };

  struct ChatCacheSnapshot
  {
    ChatChannelId           channelId{};
    Domain::ChatChannelKind kind{Domain::ChatChannelKind::Global};
    std::size_t             capacity{};
    // Ascending MessageId, independently of timestamps and arrival order.
    std::vector<ChatMessage> messages{};
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

    static Domain::Result<ChatCache> TryCreate(
      ChatChannelId           channelId,
      std::size_t             capacity,
      Domain::ChatChannelKind kind = Domain::ChatChannelKind::Global)
    {
      if (capacity == 0)
      {
        return std::unexpected{
            Domain::Error{Domain::ErrorCode::InvalidConfig, "capacity"}
        };
      }

      return ChatCache{channelId, capacity, kind};
    }

    ChatCacheState State() const noexcept
    {
      return ChatCacheState{.channelId = channelId, .kind = kind, .capacity = capacity, .count = messages.size()};
    }

    ChatCacheSnapshot Snapshot() const
    {
      ChatCacheSnapshot result{.channelId = channelId, .kind = kind, .capacity = capacity};

      result.messages.reserve(messages.size());
      for (const auto& [id, message] : messages)
      {
        result.messages.push_back(message);
      }

      return result;
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
        if (!Domain::Chat::FitsChannel(kind, message))
        {
          return std::unexpected{
              Domain::Error{Domain::ErrorCode::ChannelMismatch, "announcement"}
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

      const auto combinedSize = messages.size() + staged.size();
      const auto evictCount   = combinedSize > capacity ? combinedSize - capacity : 0;
      result.evicted          = evictCount;

      // Build the outgoing delta before mutating live state. If one of these
      // allocations fails, the cache is still unchanged. Both maps are ordered,
      // so the first evictCount keys of their merged ordering are exactly the
      // messages that will disappear.
      result.removedMessageIds.reserve(std::min(evictCount, messages.size()));
      result.addedMessages.reserve(staged.size());

      auto retainedNew = staged.begin();
      auto existing    = messages.begin();
      for (std::size_t i = 0; i < evictCount; ++i)
      {
        const bool takeExisting = retainedNew == staged.end() || (existing != messages.end() && existing->first < retainedNew->first);

        if (takeExisting)
        {
          result.removedMessageIds.push_back(existing->first);
          ++existing;
        }
        else
        {
          // This newly accepted message is evicted before it can become visible.
          ++retainedNew;
        }
      }

      for (auto it = retainedNew; it != staged.end(); ++it)
      {
        result.addedMessages.push_back(it->second);
      }

      if (!staged.empty()) messages.merge(staged);

      for (std::size_t i = 0; i < evictCount; ++i)
      {
        messages.erase(messages.begin());
      }

      return result;
    }

private:

    explicit ChatCache(ChatChannelId channelId, std::size_t capacity, Domain::ChatChannelKind kind)
        : channelId(channelId),
          kind(kind),
          capacity(capacity)
    {}

    ChatChannelId                        channelId{};
    Domain::ChatChannelKind              kind{};
    std::size_t                          capacity{};
    std::map<ChatMessageId, ChatMessage> messages{};
  };

}
