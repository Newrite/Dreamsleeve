module;

#include <glaze/glaze.hpp>
#include <glaze/toml.hpp>

export module Dreamsleeve.Host.Names;

import std;
export import Dreamsleeve.Client.Domain;
export import Dreamsleeve.Host.UiSettings;

// One place decides what name a player is shown under, for the web UI and the
// native nameplates alike. Main-thread state; no game headers, so the tests
// compile it. Pseudonyms and the ignore list are local: never sent to a server.
export namespace Dreamsleeve::Host
{

  // Shown when no checked name is available.
  constexpr std::string_view NeutralName = "Путник";

  enum class NameMode
  {
    Username,
    Display,
    Character
  };

  NameMode ModeOf(std::string_view name)
  {
    if (name == "username") return NameMode::Username;
    if (name == "character") return NameMode::Character;
    return NameMode::Display;
  }

  // Character: the published name at that moment, else the (server-checked)
  // display name, else the neutral name. Server placeholders already replace
  // stored names that fail moderation, so nothing here re-checks text.
  std::string ResolveName(NameMode mode, const Domain::PlayerData& profile, const std::optional<Domain::CharacterName>& character)
  {
    if (mode == NameMode::Character && character && !character->empty()) return *character;
    if (mode == NameMode::Username && !profile.username.empty()) return profile.username;
    if (!profile.displayName.empty()) return profile.displayName;
    return std::string{NeutralName};
  }

  namespace NamesDetail
  {

    // Skyrim-flavoured epithets; the client file may replace them.
    constexpr std::string_view BuiltInAliases[] = {
        "Странник",
        "Следопыт",
        "Бард",
        "Изгнанник",
        "Наёмник",
        "Паломник",
        "Охотник",
        "Кузнец",
        "Травник",
        "Мореход",
        "Караванщик",
        "Лучник",
        "Страж",
        "Скиталец",
        "Отшельник",
        "Алхимик",
        "Чародей",
        "Воитель",
        "Рудокоп",
        "Лесоруб",
        "Рыбак",
        "Менестрель",
        "Ловчий",
        "Книжник",
    };

    constexpr std::size_t MaxAliasBytes      = 48;
    constexpr std::size_t MaxDictionarySize  = 1024;
    constexpr std::size_t MaxDictionaryBytes = 65536;

    struct AliasFile
    {
      int                      version{1};
      std::vector<std::string> names;
    };

    // Plain printable UTF-8 of a sane length; anything else falls back.
    bool ValidAlias(std::string_view text)
    {
      if (text.empty() || text.size() > MaxAliasBytes || text.front() == ' ' || text.back() == ' ') return false;
      for (std::size_t index = 0; index < text.size();)
      {
        const auto  lead = static_cast<unsigned char>(text[index]);
        std::size_t size = lead < 0x80 ? 1 : (lead >> 5) == 0x6 ? 2 : (lead >> 4) == 0xE ? 3 : (lead >> 3) == 0x1E ? 4 : 0;
        if (size == 0 || index + size > text.size()) return false;
        if (size == 1 && (lead < 0x20 || lead == 0x7F || lead == '<' || lead == '>')) return false;
        for (std::size_t next = 1; next < size; ++next)
          if ((static_cast<unsigned char>(text[index + next]) & 0xC0) != 0x80) return false;
        index += size;
      }
      return true;
    }

    std::vector<std::string> BuiltIn()
    {
      return {std::begin(BuiltInAliases), std::end(BuiltInAliases)};
    }

  }

  struct AliasDictionary
  {
    std::vector<std::string> names;
    std::string              warning;  // Why the built-in list is used, if it is.
  };

  // A missing, empty, oversized or unreadable file uses the built-in list;
  // invalid entries are dropped individually. The result is never empty.
  AliasDictionary LoadAliasDictionary(const std::filesystem::path& path)
  {
    using namespace NamesDetail;
    const auto fallback = [](std::string reason) {
      return AliasDictionary{BuiltIn(), std::move(reason)};
    };

    std::error_code probe;
    if (!std::filesystem::exists(path, probe)) return fallback("Alias dictionary not found; using built-in names");
    std::ifstream input{path, std::ios::binary | std::ios::ate};
    if (!input) return fallback("Cannot open alias dictionary; using built-in names");
    const auto length = input.tellg();
    if (length <= 0 || length > static_cast<std::streamoff>(MaxDictionaryBytes))
      return fallback("Alias dictionary is empty or too large; using built-in names");
    std::string source(static_cast<std::size_t>(length), '\0');
    input.seekg(0);
    if (!input.read(source.data(), static_cast<std::streamsize>(source.size())))
      return fallback("Cannot read alias dictionary; using built-in names");

    AliasFile file;
    if (auto error = glz::read<glz::opts{.format = glz::TOML, .error_on_unknown_keys = false}>(file, source))
      return fallback("Invalid alias dictionary: " + glz::format_error(error, source));

    AliasDictionary       result;
    std::set<std::string> seen;
    for (auto& name : file.names)
      if (NamesDetail::ValidAlias(name) && result.names.size() < MaxDictionarySize && seen.insert(name).second)
        result.names.push_back(std::move(name));
    if (result.names.empty()) return fallback("Alias dictionary has no valid names; using built-in names");
    return result;
  }

  class Names final
  {
public:

    // The scope is the server address: IDs of different servers never meet.
    void Configure(std::string scopeName, std::vector<std::string> dictionary)
    {
      scope      = std::move(scopeName);
      aliasNames = dictionary.empty() ? NamesDetail::BuiltIn() : std::move(dictionary);
    }

    void Load(NameBook loaded)
    {
      book = Normalize(std::move(loaded));
      Reindex();
    }

    const NameBook& Book() const noexcept
    {
      return book;
    }

    const std::string& Scope() const noexcept
    {
      return scope;
    }

    // Persistence is batched by the caller; this reports unsaved changes once.
    bool TakeDirty() noexcept
    {
      return std::exchange(dirty, false);
    }

    // Stable local pseudonym: picked at random from the dictionary, never
    // derived from a real name; equal picks get a short number.
    std::string Alias(Domain::PlayerId id)
    {
      const auto key = Key(scope, std::to_string(id));
      if (const auto found = aliasIndex.find(key); found != aliasIndex.end()) return book.aliases[found->second].name;

      std::unordered_set<std::string> used;
      for (const auto& record : book.aliases)
        if (record.server == scope) used.insert(record.name);
      std::uniform_int_distribution<std::size_t> pick{0, aliasNames.size() - 1};
      const auto&                                base = aliasNames[pick(random)];
      auto                                       name = base;
      for (std::size_t number = 2; used.contains(name); ++number)
        name = base + " " + std::to_string(number);

      if (book.aliases.size() >= MaxAliasRecords)
      {
        // Oldest first: a long-unseen account may get a new pseudonym later.
        book.aliases.erase(book.aliases.begin());
        book.aliases.push_back({scope, std::to_string(id), name});
        Reindex();
      }
      else
      {
        book.aliases.push_back({scope, std::to_string(id), name});
        aliasIndex.emplace(key, book.aliases.size() - 1);
      }
      dirty = true;
      return name;
    }

    // The one name for a player on every surface under the current settings.
    std::string NameFor(
      Domain::PlayerId                            id,
      const Domain::PlayerData&                   profile,
      const std::optional<Domain::CharacterName>& character,
      const UiSettings&                           settings)
    {
      if (settings.streamerMode) return Alias(id);
      return ResolveName(ModeOf(settings.nameMode), profile, character);
    }

    bool Ignored(Domain::PlayerId id) const
    {
      return id != 0 && ignoredIndex.contains(Key(scope, std::to_string(id)));
    }

    // A personal filter: system messages (no author) and self cannot be ignored.
    bool Ignore(Domain::PlayerId id, std::optional<Domain::PlayerId> self, const Domain::PlayerData* known)
    {
      if (id == 0 || (self && *self == id) || Ignored(id) || scope.empty()) return false;
      if (book.ignored.size() >= MaxIgnoredRecords) return false;
      IgnoredRecord record{scope, std::to_string(id)};
      if (known)
      {
        record.displayName = known->displayName;
        record.username    = known->username;
      }
      book.ignored.push_back(std::move(record));
      ignoredIndex.insert(Key(scope, std::to_string(id)));
      dirty = true;
      return true;
    }

    bool Unignore(Domain::PlayerId id)
    {
      const auto text    = std::to_string(id);
      const auto removed = std::erase_if(book.ignored, [&](const auto& record) { return record.server == scope && record.id == text; });
      if (removed == 0) return false;
      ignoredIndex.erase(Key(scope, text));
      dirty = true;
      return true;
    }

    struct IgnoredEntry
    {
      Domain::PlayerId id{};
      std::string      name;
    };

    // Only this server's entries, named under the current settings.
    std::vector<IgnoredEntry> IgnoredList(const UiSettings& settings)
    {
      std::vector<IgnoredEntry> list;
      for (const auto& record : book.ignored)
      {
        if (record.server != scope) continue;
        Domain::PlayerId id{};
        const auto       parsed = std::from_chars(record.id.data(), record.id.data() + record.id.size(), id);
        if (parsed.ec != std::errc{} || id == 0) continue;
        const Domain::PlayerData known{id, record.username, record.displayName};
        list.push_back({id, NameFor(id, known, std::nullopt, settings)});
      }
      return list;
    }

private:

    static std::string Key(std::string_view server, std::string_view id)
    {
      std::string key{server};
      key += '#';
      key += id;
      return key;
    }

    void Reindex()
    {
      aliasIndex.clear();
      ignoredIndex.clear();
      for (std::size_t index = 0; index < book.aliases.size(); ++index)
        aliasIndex.insert_or_assign(Key(book.aliases[index].server, book.aliases[index].id), index);
      for (const auto& record : book.ignored)
        ignoredIndex.insert(Key(record.server, record.id));
    }

    std::string                                  scope;
    std::vector<std::string>                     aliasNames{NamesDetail::BuiltIn()};
    NameBook                                     book;
    std::unordered_map<std::string, std::size_t> aliasIndex;
    std::unordered_set<std::string>              ignoredIndex;
    std::mt19937                                 random{std::random_device{}()};
    bool                                         dirty{};
  };

}
