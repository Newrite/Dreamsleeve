export module Dreamsleeve.Host.Notices;

import std;

// The plugin's cross-thread notices. The handle parameter is an owned engine
// handle value in Runtime, and an ordinary owned value in game-free tests.
export namespace Dreamsleeve::Host::Notices
{

  enum class Kind
  {
    NewGame,       // Finished CharGen; world readiness still needs a frame probe.
    PreLoadGame,   // The previous world becomes stale at this barrier.
    PostLoadGame,  // flag = load result, not a guarantee of cell/3D readiness.
    SaveGame,      // Before file writing; visuals are already temporary.
    MenuChanged,
    ActivationKey,
    PlayerDeath,
    PlayerActivated,
    UiHidden,
    ActivationKeyF2,
    ResumeLogin,
    Disconnect
#ifdef DREAMSLEEVE_DIAGNOSTICS
      ,
    PhantomRecordingStart,
    PhantomRecordingStop,
    PhantomReplayStart,
    PhantomReplayStop
#endif
  };

  template <class Handle>
  struct Notice
  {
    Kind          kind{};
    bool          flag{};    // Load result, dead, user preference or recording duration.
    std::uint32_t formId{};  // Activated object, or diagnostic scenario.
    Handle        handle{};  // PlayerDeath: owned killer handle, resolved only by the frame.
  };

  enum class Admission
  {
    Queued,
    Coalesced,
    Busy,
    Dropped,
    Ignored
  };

  // Preserve the existing ordinary payload budget. The extra lifecycle gaps
  // are derived from its separators, not an independently growing queue.
  constexpr std::size_t MaxOrdinaryNotices = 64;

  namespace Detail
  {

    enum class Policy
    {
      Lifecycle,
      Realtime,
      Reliable,
      Ignore
    };

    constexpr Policy Delivery(Kind kind)
    {
      switch (kind)
      {
        case Kind::NewGame:
        case Kind::PreLoadGame:
        case Kind::PostLoadGame:
          return Policy::Lifecycle;
        case Kind::SaveGame:
          return Policy::Ignore;  // Temporary visuals are marked at creation.
        case Kind::MenuChanged:
        case Kind::ActivationKey:
        case Kind::PlayerDeath:
        case Kind::PlayerActivated:
          return Policy::Realtime;
        case Kind::UiHidden:
        case Kind::ActivationKeyF2:
        case Kind::ResumeLogin:
        case Kind::Disconnect:
#ifdef DREAMSLEEVE_DIAGNOSTICS
        case Kind::PhantomRecordingStart:
        case Kind::PhantomRecordingStop:
        case Kind::PhantomReplayStart:
        case Kind::PhantomReplayStop:
#endif
          return Policy::Reliable;
      }
      std::unreachable();  // Kind is internal, never an unchecked wire enum.
    }

  }

  template <class Handle>
  class Inbox
  {
    using Value = Notice<Handle>;

    struct LifecycleGap
    {
      std::optional<Value> beforeBegin;
      std::optional<Value> begin;
      std::optional<Value> afterBegin;

      Admission Post(Value notice)
      {
        if (notice.kind == Kind::PostLoadGame)
        {
          auto&      slot     = begin ? afterBegin : beforeBegin;
          const bool replaced = slot.has_value();
          if (!begin && replaced) return Admission::Coalesced;
          slot = std::move(notice);
          return replaced ? Admission::Coalesced : Admission::Queued;
        }
        if (begin)
        {
          // A later load attempt invalidates an earlier completion. Its first
          // barrier already cleared the context; nothing reenters Playing in
          // this lifecycle-only gap before the frame's final readiness probe.
          afterBegin.reset();
          return Admission::Coalesced;
        }
        begin = std::move(notice);
        return Admission::Queued;
      }

      void Take(std::vector<Value>& output)
      {
        for (auto* slot : {&beforeBegin, &begin, &afterBegin})
        {
          if (*slot) output.push_back(std::move(**slot));
          slot->reset();
        }
      }
    };

    std::mutex                                       mutex;
    std::array<Value, MaxOrdinaryNotices>            ordinary{};
    std::array<LifecycleGap, MaxOrdinaryNotices + 1> gaps{};
    std::size_t                                      count{};
    bool                                             overflow{};

public:

    Admission Post(Value notice)
    {
      const auto policy = Detail::Delivery(notice.kind);
      if (policy == Detail::Policy::Ignore) return Admission::Ignored;

      std::lock_guard lock{mutex};
      if (policy == Detail::Policy::Lifecycle) return gaps[count].Post(std::move(notice));
      if (count == MaxOrdinaryNotices)
      {
        if (policy == Detail::Policy::Reliable) return Admission::Busy;
        overflow = true;
        return Admission::Dropped;
      }
      ordinary[count++] = std::move(notice);
      return Admission::Queued;
    }

    // The consumer owns output. A lifecycle gap never crosses any admitted
    // ordinary notice, including a menu hint or a diagnostic stop/start.
    bool Take(std::vector<Value>& output)
    {
      output.clear();
      output.reserve(MaxOrdinaryNotices + 3 * (MaxOrdinaryNotices + 1));
      std::lock_guard lock{mutex};
      for (std::size_t index = 0; index <= count; ++index)
      {
        gaps[index].Take(output);
        if (index < count)
        {
          output.push_back(std::move(ordinary[index]));
          ordinary[index] = {};
        }
      }
      count = 0;
      return std::exchange(overflow, false);
    }
  };

}
