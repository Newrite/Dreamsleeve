module;
#include <process.h>
#include <cerrno>

export module Dreamsleeve.Client.Auth.Browser;

import std;
import Dreamsleeve.Client.Auth;

namespace Dreamsleeve::Client::Auth::Browser
{

  class Owner;

}

export namespace Dreamsleeve::Client::Auth::Browser
{

  enum class Failure
  {
    InvalidPage,
    Busy,
    Launch
  };

  struct Error
  {
    Failure     kind;
    std::string detail;
  };

  // A consumer observes only this opening's immutable completion. Dropping it
  // does not release the process-wide shell operation or admit another opener.
  class Opening final
  {
public:

    std::optional<Result<std::string>> Read() const
    {
      std::lock_guard lock{state->mutex};
      return state->result;
    }

private:

    friend class Owner;

    struct State
    {
      std::mutex                         mutex;
      std::optional<Result<std::string>> result;
    };

    explicit Opening(std::shared_ptr<State> owned) : state(std::move(owned)) {}

    std::shared_ptr<State> state;
  };

  using Handle      = std::shared_ptr<const Opening>;
  using StartResult = std::expected<Handle, Error>;

}

namespace Dreamsleeve::Client::Auth::Browser
{

  // One shell opener in the process, including one whose sign-in/app was
  // abandoned. The job contains owned inputs only, never an application pointer.
  class Owner final
  {
public:

    static StartResult Begin(std::string page, std::function<Result<std::string>(std::string_view)> open)
    {
      if (!SteamPage(page))
        return std::unexpected{
            Error{Failure::InvalidPage, "Not a Steam sign-in page"}
        };

      auto            owner = Shared();
      std::lock_guard lock{owner->mutex};
      if (owner->pending)
        return std::unexpected{
            Error{Failure::Busy, "A previous browser opening is still pending; copy this link to sign in"}
        };

      auto   state = std::make_shared<Opening::State>();
      Handle consumer{new Opening{state}};
      auto   job = std::make_unique<Job>(std::move(page), std::move(open), state, owner);
      auto*  raw = job.release();

      // _beginthread owns its automatically-closed handle; never wait/close it.
      // https://learn.microsoft.com/en-us/cpp/c-runtime-library/reference/beginthread-beginthreadex
      const bool refused = owner->failNextLaunch.exchange(false);
      const auto thread  = refused ? static_cast<std::uintptr_t>(-1) : _beginthread(Run, 0, raw);
      if (thread == static_cast<std::uintptr_t>(-1))
      {
        job.reset(raw);
        return std::unexpected{
            Error{
              Failure::Launch,
              "Cannot start browser opener: " + std::error_code{refused ? EAGAIN : errno, std::generic_category()}.message()
            }
        };
      }

      owner->pending = std::move(state);
      return consumer;
    }

    static bool Pending()
    {
      auto            owner = Shared();
      std::lock_guard lock{owner->mutex};
      return static_cast<bool>(owner->pending);
    }

    static void FailNextLaunch()
    {
      Shared()->failNextLaunch = true;
    }

private:

    struct Resource
    {
      std::mutex                      mutex;
      std::shared_ptr<Opening::State> pending;
      std::atomic_bool                failNextLaunch{};
    };

    static std::shared_ptr<Resource> Shared()
    {
      // A running job retains this owner during process-static teardown too.
      static const auto owner = std::make_shared<Resource>();
      return owner;
    }

    struct Job
    {
      std::string                                          page;
      std::function<Result<std::string>(std::string_view)> open;
      std::shared_ptr<Opening::State>                      state;
      std::shared_ptr<Resource>                            owner;
    };

    static void __cdecl Run(void* value)
    {
      std::unique_ptr<Job> job{static_cast<Job*>(value)};
      auto                 result = job->open(job->page);

      std::lock_guard      lock{job->owner->mutex};
      job->owner->pending.reset();
      std::lock_guard completion{job->state->mutex};
      job->state->result = std::move(result);
    }
  };

}

export namespace Dreamsleeve::Client::Auth::Browser
{

  StartResult Start(std::string page)
  {
    return Owner::Begin(std::move(page), OpenSteamPage);
  }

  namespace Testing
  {

    // Causal shell/CRT creation gates, confined to this browser lifecycle.
    StartResult Start(std::string page, std::function<Result<std::string>(std::string_view)> open)
    {
      return Owner::Begin(std::move(page), std::move(open));
    }

    bool Pending()
    {
      return Owner::Pending();
    }

    void FailNextLaunch()
    {
      Owner::FailNextLaunch();
    }

  }
}
