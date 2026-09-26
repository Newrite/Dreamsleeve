export module Dreamsleeve.Client.SnapshotMailbox;

import std;

export import Dreamsleeve.Client.Model;

export namespace Dreamsleeve::Client
{

  // One publishing owner, any number of readers. It publishes the latest full
  // state, not a queue of every event. Intermediate snapshots may be superseded.
  // atomic<shared_ptr> is thread-safe; the implementation need not be lock-free.
  class SnapshotMailbox final
  {
public:

    SnapshotMailbox()                                  = default;
    SnapshotMailbox(const SnapshotMailbox&)            = delete;
    SnapshotMailbox& operator=(const SnapshotMailbox&) = delete;
    SnapshotMailbox(SnapshotMailbox&&)                 = delete;
    SnapshotMailbox& operator=(SnapshotMailbox&&)      = delete;

    void Publish(ClientSnapshot snapshot)
    {
      // Construct an actually const object; no mutable shared_ptr alias escapes.
      latest.store(std::make_shared<const ClientSnapshot>(std::move(snapshot)), std::memory_order_release);
    }

    std::shared_ptr<const ClientSnapshot> Read() const noexcept
    {
      return latest.load(std::memory_order_acquire);
    }

private:

    std::atomic<std::shared_ptr<const ClientSnapshot>> latest{};
  };

}
