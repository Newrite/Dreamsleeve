export module Dreamsleeve.Host.Bubbles;

import std;
export import Dreamsleeve.Client.Domain;
export import Dreamsleeve.Host.UiSettings;

// One active chat message per remote player with its display timer. Main-thread
// state only: it knows nothing about fireflies, the HUD or visibility, so the
// timer keeps running while a player is occluded or off screen and a message
// never waits for its author to appear. No game headers: compiled into the tests.
export namespace Dreamsleeve::Host
{

  class Bubbles final
  {
public:

    using Clock = std::chrono::steady_clock;

    struct Active
    {
      std::string_view text;
      float            alpha{1.0f};  // 1 while shown, falling to 0 across the fade.
    };

    // A new message replaces the previous text and restarts the timer.
    void Post(Domain::PlayerId author, std::string text, Clock::time_point now)
    {
      entries.insert_or_assign(author, Entry{std::move(text), now});
    }

    void Erase(Domain::PlayerId author)
    {
      entries.erase(author);
    }

    void Clear()
    {
      entries.clear();
    }

    // Drops expired messages and those of players that no longer qualify.
    template <class Keep>
    void Prune(Clock::time_point now, const UiSettings& settings, Keep keep)
    {
      std::erase_if(entries, [&](const auto& entry) { return !keep(entry.first) || !Alpha(entry.second, now, settings); });
    }

    std::optional<Active> Find(Domain::PlayerId author, Clock::time_point now, const UiSettings& settings) const
    {
      const auto found = entries.find(author);
      if (found == entries.end()) return std::nullopt;
      const auto alpha = Alpha(found->second, now, settings);
      if (!alpha) return std::nullopt;
      return Active{found->second.text, *alpha};
    }

    std::size_t Size() const noexcept
    {
      return entries.size();
    }

private:

    struct Entry
    {
      std::string       text;
      Clock::time_point shownAt{};
    };

    // Elapsed time alone decides: visibility never pauses or restarts the timer.
    static std::optional<float> Alpha(const Entry& entry, Clock::time_point now, const UiSettings& settings)
    {
      const auto elapsed = std::chrono::duration<double>(now - entry.shownAt).count();
      if (elapsed < settings.bubbleDuration) return 1.0f;
      if (!settings.bubbleFade) return std::nullopt;
      const auto fade = (elapsed - settings.bubbleDuration) / settings.bubbleFadeDuration;
      if (fade >= 1.0) return std::nullopt;
      return static_cast<float>(1.0 - fade);
    }

    std::unordered_map<Domain::PlayerId, Entry> entries;
  };

}
