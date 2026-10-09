#include <doctest/doctest.h>

import std;
import Dreamsleeve.Host.Notices;

namespace N  = Dreamsleeve::Host::Notices;
using Notice = N::Notice<std::uint32_t>;

TEST_SUITE_BEGIN("Host.Notices");

TEST_CASE("Saturation preserves an admitted disconnect and the later world barrier")
{
  N::Inbox<std::uint32_t> inbox;
  REQUIRE(inbox.Post({N::Kind::Disconnect}) == N::Admission::Queued);
  for (std::uint32_t id = 1; id < N::MaxOrdinaryNotices; ++id)
    REQUIRE(inbox.Post({N::Kind::PlayerActivated, false, id}) == N::Admission::Queued);

  CHECK(inbox.Post({N::Kind::ResumeLogin}) == N::Admission::Busy);
  CHECK(inbox.Post({N::Kind::PlayerDeath}) == N::Admission::Dropped);
  CHECK(inbox.Post({N::Kind::PreLoadGame}) == N::Admission::Queued);
  CHECK(inbox.Post({N::Kind::PostLoadGame, true}) == N::Admission::Queued);

  std::vector<Notice> notices;
  CHECK(inbox.Take(notices));
  REQUIRE(notices.size() == N::MaxOrdinaryNotices + 2);
  CHECK(notices.front().kind == N::Kind::Disconnect);
  for (std::size_t index = 1; index < N::MaxOrdinaryNotices; ++index)
    CHECK(notices[index].formId == index);
  CHECK(notices[N::MaxOrdinaryNotices].kind == N::Kind::PreLoadGame);
  CHECK(notices.back().kind == N::Kind::PostLoadGame);
  CHECK(notices.back().flag);
  CHECK_FALSE(std::ranges::any_of(notices, [](const auto& notice) { return notice.kind == N::Kind::ResumeLogin; }));

  // A later explicit action can be admitted; the rejected action is not replayed.
  CHECK(inbox.Post({N::Kind::ResumeLogin}) == N::Admission::Queued);
  CHECK_FALSE(inbox.Take(notices));
  REQUIRE(notices.size() == 1);
  CHECK(notices.front().kind == N::Kind::ResumeLogin);
  CHECK_FALSE(inbox.Take(notices));
  CHECK(notices.empty());
}

TEST_CASE("Every SKSE menu control reports admission refusal without replacing accepted work")
{
  N::Inbox<std::uint32_t> inbox;
  for (std::uint32_t id = 0; id < N::MaxOrdinaryNotices; ++id)
    REQUIRE(inbox.Post({N::Kind::ActivationKey, false, id}) == N::Admission::Queued);

  const auto controls = std::array{
      N::Kind::UiHidden,
      N::Kind::ActivationKeyF2,
      N::Kind::ResumeLogin,
      N::Kind::Disconnect
#ifdef DREAMSLEEVE_DIAGNOSTICS
      ,
      N::Kind::PhantomRecordingStart,
      N::Kind::PhantomRecordingStop,
      N::Kind::PhantomReplayStart,
      N::Kind::PhantomReplayStop
#endif
  };
  for (const auto kind : controls)
    CHECK(inbox.Post({kind, true, 42}) == N::Admission::Busy);
  std::vector<Notice> notices;
  CHECK_FALSE(inbox.Take(notices));
  REQUIRE(notices.size() == N::MaxOrdinaryNotices);
  for (std::size_t index = 0; index < notices.size(); ++index)
  {
    CHECK(notices[index].kind == N::Kind::ActivationKey);
    CHECK(notices[index].formId == index);
  }
}

TEST_CASE("Lifecycle folding never crosses an admitted menu, telemetry, control or diagnostic notice")
{
  N::Inbox<std::uint32_t> inbox;
  const auto              sequence = std::array{
      N::Kind::PreLoadGame,
      N::Kind::PostLoadGame,
      N::Kind::MenuChanged,
      N::Kind::NewGame,
      N::Kind::PostLoadGame,
      N::Kind::PlayerDeath,
      N::Kind::PreLoadGame,
      N::Kind::PostLoadGame,
      N::Kind::UiHidden,
      N::Kind::PreLoadGame,
#ifdef DREAMSLEEVE_DIAGNOSTICS
      N::Kind::PhantomRecordingStart,
#endif
      N::Kind::PostLoadGame,
      N::Kind::Disconnect,
      N::Kind::ResumeLogin
  };
  for (const auto kind : sequence)
    REQUIRE(inbox.Post({kind, true, 77, 123}) == N::Admission::Queued);
  std::vector<Notice> notices;
  CHECK_FALSE(inbox.Take(notices));
  REQUIRE(notices.size() == sequence.size());
  for (std::size_t index = 0; index < sequence.size(); ++index)
  {
    CHECK(notices[index].kind == sequence[index]);
    CHECK(notices[index].flag);
    CHECK(notices[index].formId == 77);
    CHECK(notices[index].handle == 123);
  }
}

TEST_CASE("An adjacent lifecycle storm stays bounded while preserving each ordinary separator")
{
  N::Inbox<std::uint32_t> inbox;
  for (std::uint32_t gap = 0; gap <= N::MaxOrdinaryNotices; ++gap)
  {
    REQUIRE(inbox.Post({N::Kind::PostLoadGame, false}) == N::Admission::Queued);
    REQUIRE(inbox.Post({N::Kind::PreLoadGame}) == N::Admission::Queued);
    for (unsigned cycle = 0; cycle < 1000; ++cycle)
    {
      static_cast<void>(inbox.Post({N::Kind::PreLoadGame}));
      static_cast<void>(inbox.Post({N::Kind::PostLoadGame, cycle % 2 == 1}));
    }
    if (gap < N::MaxOrdinaryNotices) REQUIRE(inbox.Post({N::Kind::MenuChanged, false, gap}) == N::Admission::Queued);
  }
  std::vector<Notice> notices;
  CHECK_FALSE(inbox.Take(notices));
  REQUIRE(notices.size() == N::MaxOrdinaryNotices + 3 * (N::MaxOrdinaryNotices + 1));
  for (std::size_t gap = 0; gap <= N::MaxOrdinaryNotices; ++gap)
  {
    const auto offset = 4 * gap;
    CHECK(notices[offset].kind == N::Kind::PostLoadGame);
    CHECK_FALSE(notices[offset].flag);
    CHECK(notices[offset + 1].kind == N::Kind::PreLoadGame);
    CHECK(notices[offset + 2].kind == N::Kind::PostLoadGame);
    CHECK(notices[offset + 2].flag);
    if (gap < N::MaxOrdinaryNotices)
    {
      CHECK(notices[offset + 3].kind == N::Kind::MenuChanged);
      CHECK(notices[offset + 3].formId == gap);
    }
  }
  CHECK_FALSE(inbox.Take(notices));
  CHECK(notices.empty());
}

TEST_CASE("A new adjacent load attempt does not retain an earlier completion")
{
  N::Inbox<std::uint32_t> inbox;
  REQUIRE(inbox.Post({N::Kind::PreLoadGame}) == N::Admission::Queued);
  REQUIRE(inbox.Post({N::Kind::PostLoadGame, true}) == N::Admission::Queued);
  CHECK(inbox.Post({N::Kind::NewGame}) == N::Admission::Coalesced);
  CHECK(inbox.Post({N::Kind::SaveGame}) == N::Admission::Ignored);
  std::vector<Notice> notices;
  CHECK_FALSE(inbox.Take(notices));
  REQUIRE(notices.size() == 1);
  CHECK(notices.front().kind == N::Kind::PreLoadGame);
}

TEST_CASE("Notice handoff retains owned values and releases rejected values without aliases")
{
  using Handle = std::shared_ptr<const std::string>;
  N::Inbox<Handle>                 inbox;
  auto                             retained = std::make_shared<const std::string>("owned killer handle");
  std::weak_ptr<const std::string> owner    = retained;
  REQUIRE(inbox.Post({N::Kind::PlayerDeath, false, 0, std::move(retained)}) == N::Admission::Queued);
  CHECK_FALSE(owner.expired());
  for (std::size_t index = 1; index < N::MaxOrdinaryNotices; ++index)
    REQUIRE(inbox.Post({N::Kind::ActivationKey}) == N::Admission::Queued);

  auto                             rejected = std::make_shared<const std::string>("rejected value");
  std::weak_ptr<const std::string> refused  = rejected;
  CHECK(inbox.Post({N::Kind::Disconnect, false, 0, std::move(rejected)}) == N::Admission::Busy);
  CHECK(refused.expired());

  std::vector<N::Notice<Handle>> notices;
  CHECK_FALSE(inbox.Take(notices));
  REQUIRE(notices.front().handle);
  CHECK(*notices.front().handle == "owned killer handle");
  CHECK_FALSE(owner.expired());

  notices.clear();
  CHECK(owner.expired());
}

TEST_SUITE_END();
