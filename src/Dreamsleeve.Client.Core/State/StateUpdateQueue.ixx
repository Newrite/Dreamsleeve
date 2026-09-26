export module Dreamsleeve.Client.StateUpdateQueue;

import std;

export import Dreamsleeve.Client.StateUpdate;

export namespace Dreamsleeve::Client
{

  enum class StatePublishResult
  {
    Queued,
    SnapshotRequired
  };

  struct StateUpdateBatch final
  {
    std::vector<ClientStateUpdate> updates;
    bool                           requiresSnapshot{};
  };

  // One model owner publishes; one consumer drains and applies batches in order.
  // Capacity bounds queued envelopes, not the bytes inside their payloads.
  class StateUpdateQueue final
  {
public:

    using Ptr = std::unique_ptr<StateUpdateQueue>;

    static Domain::Result<Ptr> TryCreate(std::size_t capacity)
    {
      if (capacity == 0)
        return std::unexpected{
            Domain::Error{Domain::ErrorCode::InvalidConfig, "capacity"}
        };
      return Ptr{new StateUpdateQueue{capacity}};
    }

    StateUpdateQueue(const StateUpdateQueue&)            = delete;
    StateUpdateQueue& operator=(const StateUpdateQueue&) = delete;
    StateUpdateQueue(StateUpdateQueue&&)                 = delete;
    StateUpdateQueue& operator=(StateUpdateQueue&&)      = delete;

    StatePublishResult Publish(ClientStateUpdate update)
    {
      std::lock_guard lock{mutex};
      if (std::holds_alternative<ClientSnapshot>(update))
      {
        // A fresh snapshot supersedes everything not yet taken by the consumer.
        requiresSnapshot = true;
        pending.clear();
        pending.push_back(std::move(update));
        requiresSnapshot = false;
        return StatePublishResult::Queued;
      }

      if (requiresSnapshot) return StatePublishResult::SnapshotRequired;
      if (pending.size() >= maxPending)
      {
        pending.clear();
        requiresSnapshot = true;
        return StatePublishResult::SnapshotRequired;
      }

      pending.push_back(std::move(update));
      return StatePublishResult::Queued;
    }

    bool RequiresSnapshot() const
    {
      std::lock_guard lock{mutex};
      return requiresSnapshot;
    }

    void TakeAll(StateUpdateBatch& output)
    {
      output.updates.clear();
      std::lock_guard lock{mutex};
      pending.swap(output.updates);
      output.requiresSnapshot = requiresSnapshot;
    }

private:

    explicit StateUpdateQueue(std::size_t capacity) : maxPending{capacity} {}

    const std::size_t              maxPending;
    mutable std::mutex             mutex;
    std::vector<ClientStateUpdate> pending;
    bool                           requiresSnapshot{true};
  };

}
