#include <doctest/doctest.h>

import std;
import Dreamsleeve.Client.StateUpdateQueue;

namespace
{

  using namespace Dreamsleeve::Client;
  using namespace Domain;

  ClientSnapshot Snapshot(std::uint64_t revision, std::uint64_t generation = 1)
  {
    return {.generation = generation, .revision = revision};
  }

  ClientStateDelta Delta(std::uint64_t revision)
  {
    return {.generation = 1, .revision = revision};
  }

  std::uint64_t Revision(const ClientStateUpdate& update)
  {
    return std::visit([](const auto& value) { return value.revision; }, update);
  }

  StateUpdateQueue::Ptr Queue(std::size_t capacity)
  {
    auto result = StateUpdateQueue::TryCreate(capacity);
    REQUIRE(result);
    return std::move(*result);
  }

  void Initialize(StateUpdateQueue& queue)
  {
    REQUIRE(queue.Publish(Snapshot(0)) == StatePublishResult::Queued);
    StateUpdateBatch batch;
    queue.TakeAll(batch);
    REQUIRE_FALSE(batch.requiresSnapshot);
    REQUIRE(batch.updates.size() == 1);
  }

}

TEST_SUITE_BEGIN("Client.StateUpdateQueue");

TEST_CASE("State queue requires a positive capacity and starts awaiting a snapshot")
{
  const auto invalid = StateUpdateQueue::TryCreate(0);
  REQUIRE_FALSE(invalid);
  CHECK(invalid.error().code == ErrorCode::InvalidConfig);
  CHECK(invalid.error().field == "capacity");
  auto queue = Queue(2);
  CHECK(queue->RequiresSnapshot());
  CHECK(queue->Publish(Delta(1)) == StatePublishResult::SnapshotRequired);
  StateUpdateBatch batch;
  queue->TakeAll(batch);
  CHECK(batch.requiresSnapshot);
  CHECK(batch.updates.empty());
  Initialize(*queue);
  CHECK_FALSE(queue->RequiresSnapshot());
}

TEST_CASE("State queue drains ordered deltas and replaces the reused output batch")
{
  auto queue = Queue(3);
  Initialize(*queue);
  for (std::uint64_t revision = 1; revision <= 3; ++revision)
    REQUIRE(queue->Publish(Delta(revision)) == StatePublishResult::Queued);

  StateUpdateBatch batch;
  queue->TakeAll(batch);
  CHECK_FALSE(batch.requiresSnapshot);
  REQUIRE(batch.updates.size() == 3);
  for (std::size_t i = 0; i < batch.updates.size(); ++i)
  {
    CHECK(std::holds_alternative<ClientStateDelta>(batch.updates[i]));
    CHECK(Revision(batch.updates[i]) == i + 1);
  }
  queue->TakeAll(batch);
  CHECK(batch.updates.empty());
  CHECK_FALSE(batch.requiresSnapshot);
}

TEST_CASE("State queue overflow discards the incomplete chain until a fresh snapshot arrives")
{
  auto queue = Queue(2);
  Initialize(*queue);
  REQUIRE(queue->Publish(Delta(1)) == StatePublishResult::Queued);
  REQUIRE(queue->Publish(Delta(2)) == StatePublishResult::Queued);
  CHECK(queue->Publish(Delta(3)) == StatePublishResult::SnapshotRequired);
  CHECK(queue->RequiresSnapshot());

  StateUpdateBatch batch;
  queue->TakeAll(batch);
  CHECK(batch.requiresSnapshot);
  CHECK(batch.updates.empty());
  CHECK(queue->Publish(Delta(4)) == StatePublishResult::SnapshotRequired);
  queue->TakeAll(batch);
  CHECK(batch.requiresSnapshot);  // Reading must not acknowledge recovery.
  CHECK(batch.updates.empty());

  REQUIRE(queue->Publish(Snapshot(4)) == StatePublishResult::Queued);
  REQUIRE(queue->Publish(Delta(5)) == StatePublishResult::Queued);
  queue->TakeAll(batch);
  CHECK_FALSE(batch.requiresSnapshot);
  REQUIRE(batch.updates.size() == 2);
  CHECK(std::holds_alternative<ClientSnapshot>(batch.updates[0]));
  CHECK(Revision(batch.updates[0]) == 4);
  CHECK(std::holds_alternative<ClientStateDelta>(batch.updates[1]));
  CHECK(Revision(batch.updates[1]) == 5);
}

TEST_CASE("State queue accepts a superseding snapshot even at capacity one")
{
  auto queue = Queue(1);
  REQUIRE(queue->Publish(Snapshot(1)) == StatePublishResult::Queued);
  REQUIRE(queue->Publish(Snapshot(2, 2)) == StatePublishResult::Queued);
  StateUpdateBatch batch;
  queue->TakeAll(batch);
  REQUIRE(batch.updates.size() == 1);
  REQUIRE(std::holds_alternative<ClientSnapshot>(batch.updates.front()));
  CHECK(std::get<ClientSnapshot>(batch.updates.front()).generation == 2);
  CHECK(Revision(batch.updates.front()) == 2);
  CHECK_FALSE(batch.requiresSnapshot);
}

TEST_CASE("State queue recovery does not mutate a batch already owned by the consumer")
{
  auto queue = Queue(1);
  Initialize(*queue);
  REQUIRE(queue->Publish(Delta(1)) == StatePublishResult::Queued);
  StateUpdateBatch previous;
  queue->TakeAll(previous);
  REQUIRE(queue->Publish(Delta(2)) == StatePublishResult::Queued);
  REQUIRE(queue->Publish(Delta(3)) == StatePublishResult::SnapshotRequired);
  REQUIRE(queue->Publish(Snapshot(3)) == StatePublishResult::Queued);
  StateUpdateBatch current;
  queue->TakeAll(current);

  REQUIRE(previous.updates.size() == 1);
  CHECK(Revision(previous.updates.front()) == 1);
  CHECK_FALSE(previous.requiresSnapshot);
  REQUIRE(current.updates.size() == 1);
  CHECK(Revision(current.updates.front()) == 3);
}

TEST_CASE("State queue restores chat contents after overflow and delivers subsequent eviction")
{
  ClientModel model;
  ChangeBatch scratch;
  auto        queue = Queue(2);
  REQUIRE(model.RegisterChannel(1, 2));
  TakeStateUpdate(model, scratch);  // Baseline includes registration.
  REQUIRE(queue->Publish(model.Snapshot()) == StatePublishResult::Queued);

  for (ChatMessageId id = 1; id <= 3; ++id)
  {
    ChatMessage message{
        id,
        1,
        {7, "player", "Display"},
        "Message",
        MessageTime{}
    };
    REQUIRE(model.Apply(model.Generation(), ChatMessagesReceived{1, {message}}));
    auto update = TakeStateUpdate(model, scratch);
    REQUIRE(update);
    if (queue->Publish(std::move(*update)) == StatePublishResult::SnapshotRequired)
      REQUIRE(queue->Publish(model.Snapshot()) == StatePublishResult::Queued);
  }

  StateUpdateBatch batch;
  queue->TakeAll(batch);
  CHECK_FALSE(batch.requiresSnapshot);
  REQUIRE(batch.updates.size() == 2);
  REQUIRE(std::holds_alternative<ClientSnapshot>(batch.updates[0]));
  const auto& snapshot = std::get<ClientSnapshot>(batch.updates[0]);
  REQUIRE(snapshot.chats.size() == 1);
  auto messages = snapshot.chats.front().messages;
  REQUIRE(messages.size() == 2);
  CHECK(messages[0].messageId == 1);
  CHECK(messages[1].messageId == 2);
  REQUIRE(std::holds_alternative<ClientStateDelta>(batch.updates[1]));
  const auto& delta = std::get<ClientStateDelta>(batch.updates[1]);
  for (const auto& change : delta.chatContent)
  {
    if (const auto* removed = std::get_if<ChatMessagesRemoved>(&change))
      std::erase_if(messages, [&](const auto& message) {
        return std::ranges::find(removed->messageIds, message.messageId) != removed->messageIds.end();
      });
    else
    {
      const auto& added = std::get<ChatMessagesAdded>(change).messages;
      messages.insert(messages.end(), added.begin(), added.end());
    }
  }
  CHECK(messages == model.FindChat(1)->messages);
}

TEST_CASE("State queue hands off coherent ordered batches between two threads")
{
  auto                            queue = Queue(2);
  std::barrier                    phase{2};
  std::vector<StatePublishResult> results;
  std::vector<std::uint64_t>      received;
  std::vector<bool>               recovery;
  {
    std::jthread     producer{[&] {
      results.push_back(queue->Publish(Snapshot(0)));
      phase.arrive_and_wait();
      phase.arrive_and_wait();
      for (std::uint64_t i = 1; i <= 32; ++i)
      {
        results.push_back(queue->Publish(Delta(i * 2 - 1)));
        results.push_back(queue->Publish(Delta(i * 2)));
        phase.arrive_and_wait();
        phase.arrive_and_wait();
      }
    }};
    StateUpdateBatch batch;
    for (int i = 0; i <= 32; ++i)
    {
      phase.arrive_and_wait();
      queue->TakeAll(batch);
      phase.arrive_and_wait();  // Producer may proceed while this batch is read.
      recovery.push_back(batch.requiresSnapshot);
      for (const auto& update : batch.updates)
        received.push_back(Revision(update));
    }
  }
  REQUIRE(received.size() == 65);
  for (std::size_t i = 0; i < received.size(); ++i)
    CHECK(received[i] == i);
  CHECK(std::ranges::all_of(results, [](auto result) { return result == StatePublishResult::Queued; }));
  CHECK(std::ranges::none_of(recovery, [](bool value) { return value; }));
}

TEST_SUITE_END();
