module;
#include <glaze/toml.hpp>

export module Dreamsleeve.Client.Settings;

import std;
import Dreamsleeve.Client.Auth;
export import Dreamsleeve.Client.Config;

export namespace Dreamsleeve::Client
{

  constexpr int ClientSettingsVersion = 1;

  struct ClientSettings
  {
    Configuration client{};
    std::string   authUrl{"http://127.0.0.1:8779"};
    std::size_t   commandCapacity{8};
    std::size_t   stateCapacity{8};
    bool          allowInsecureRemoteAuth{false};
  };

}

// Address and chrono values have explicit file representations; protocol/domain
// objects retain their existing types and never depend on the TOML library.
template <>
struct glz::meta<Dreamsleeve::Client::Configuration>
{
  using T                     = Dreamsleeve::Client::Configuration;
  static constexpr auto value = object(
    "network",
    &T::network,
    "connectTimeoutMs",
    &T::connectTimeoutMs,
    "disconnectTimeoutMs",
    &T::disconnectTimeoutMs,
    "sessionTimeoutMs",
    &T::sessionTimeoutMs,
    "maxInitialPlayers",
    &T::maxInitialPlayers,
    "maxRecentMessages",
    &T::maxRecentMessages,
    "chatCapacity",
    &T::chatCapacity,
    "maxPendingChatRequests",
    &T::maxPendingChatRequests,
    "maxPendingPlayerUpdates",
    &T::maxPendingPlayerUpdates,
    "maxActorValues",
    &T::maxActorValues,
    "playerSampleIntervalMs",
    &T::playerSampleIntervalMs,
    "visibilityDistance",
    &T::visibilityDistance,
    "showFireflies",
    &T::showFireflies,
    "fireflyPlugin",
    &T::fireflyPlugin,
    "fireflyFormId",
    &T::fireflyFormId,
    "captureKeyboard",
    &T::captureKeyboard,
    "fireflyScale",
    &T::fireflyScale,
    "groundNotePlugin",
    &T::groundNotePlugin,
    "groundNoteFormId",
    &T::groundNoteFormId,
    "groundNoteScale",
    &T::groundNoteScale,
    "deathMarkPlugin",
    &T::deathMarkPlugin,
    "deathMarkFormId",
    &T::deathMarkFormId,
    "deathMarkScale",
    &T::deathMarkScale,
    "maxPendingMovementSamples",
    &T::maxPendingMovementSamples);
};

namespace Dreamsleeve::Client
{
  namespace SettingsDetail
  {

    // Glaze 7.0.2 reads values directly but does not reject duplicate keys.
    // Reuse its tokenizer to check document structure before populating settings.
    struct DocumentCheck
    {
      glz::context                       context{};
      std::set<std::vector<std::string>> keys;
      std::set<std::vector<std::string>> tables;

      bool Members(const char*& it, const char* end, std::vector<std::string> scope = {}, bool inlined = false)
      {
        while (true)
        {
          glz::skip_ws_and_comments(it, end);
          if (it == end) return !inlined;
          if (!inlined && (*it == '\n' || *it == '\r'))
          {
            ++it;
            continue;
          }
          if (inlined && *it == '}')
          {
            ++it;
            return true;
          }

          const bool table = *it == '[';
          if (table && inlined) return false;
          if (table) ++it;

          std::vector<std::string> path;
          if (!glz::parse_toml_key(path, context, it, end)) return false;

          if (table)
          {
            if (it == end || *it++ != ']' || !tables.insert(path).second || keys.contains(path)) return false;
            scope = std::move(path);
          }
          else
          {
            if (it == end || *it++ != '=') return false;
            path.insert(path.begin(), scope.begin(), scope.end());
            if (!keys.insert(path).second) return false;

            glz::skip_ws_and_comments(it, end);
            if (it == end) return false;
            if (*it == '{')
            {
              ++it;
              if (!Members(it, end, path, true)) return false;
            }
            else
            {
              glz::skip_value<glz::TOML>::op<glz::opts{.format = glz::TOML}>(context, it, end);
              if (context.error != glz::error_code::none) return false;
            }
          }

          if (inlined)
          {
            glz::skip_ws_and_comments(it, end);
            if (it == end) return false;
            if (*it == '}')
            {
              ++it;
              return true;
            }
            if (*it++ != ',') return false;
          }
        }
      }
    };

    // The file shape: chrono values in milliseconds, the address as ip and port.
    export struct InterpolationFile
    {
      std::int64_t delayMs{MovementSettings{}.delay.count()};
      std::int64_t maxGapMs{MovementSettings{}.maxGap.count()};
      std::size_t  historyCapacity{MovementSettings{}.historyCapacity};
      double       teleportDistance{MovementSettings{}.teleportDistance};
    };

    export struct SettingsFile
    {
      int               version{ClientSettingsVersion};
      std::string       serverIp{"127.0.0.1"};
      Port              serverPort{DefaultServerPort};
      std::string       authUrl{ClientSettings{}.authUrl};
      Configuration     client{};
      InterpolationFile interpolation{};
      std::size_t       commandCapacity{ClientSettings{}.commandCapacity};
      std::size_t       stateCapacity{ClientSettings{}.stateCapacity};
      bool              allowInsecureRemoteAuth{ClientSettings{}.allowInsecureRemoteAuth};
    };

  }

  // The one check of every client setting; ClientApplication::TryCreate runs it,
  // and the runtime, codec and movement view trust what passed.
  export std::expected<void, std::string> ValidateClientSettings(const ClientSettings& settings)
  {
    if (auto field = settings.client.InvalidSetting()) return std::unexpected{"Invalid client setting: " + std::string{*field}};
    if (settings.commandCapacity == 0 || settings.stateCapacity == 0) return std::unexpected{"Client exchange capacities must be positive"};
    return Auth::ValidateUrl(settings.authUrl, settings.allowInsecureRemoteAuth);
  }

  // client.example.toml, the documented defaults, as bytes from the xmake rule dreamsleeve.embed.
  export std::string_view DefaultClientToml() noexcept
  {
    static constexpr unsigned char bytes[] = {
#include "client.example.toml.h"
    };
    return {reinterpret_cast<const char*>(bytes), sizeof(bytes)};
  }

  // Writes the documented defaults when the file is absent, so the first run has a
  // file to edit; an existing file is never rewritten.
  export std::expected<void, std::string> EnsureClientSettings(const std::filesystem::path& path)
  {
    std::error_code error;
    if (std::filesystem::exists(path, error)) return {};
    std::filesystem::create_directories(path.parent_path(), error);
    if (error) return std::unexpected{"Cannot create " + path.parent_path().string()};
    std::ofstream output{path, std::ios::binary};
    if (!output || !(output << DefaultClientToml())) return std::unexpected{"Cannot write " + path.string()};
    return {};
  }

  // Parses the caller's file; values are checked by ValidateClientSettings.
  // A missing or malformed file never silently yields defaults.
  export std::expected<ClientSettings, std::string> LoadClientSettings(const std::filesystem::path& path)
  {
    std::ifstream input{path, std::ios::binary | std::ios::ate};
    if (!input) return std::unexpected{"Cannot open client configuration"};
    const auto length = input.tellg();
    if (length < 0 || length > 65536) return std::unexpected{"Client configuration must contain 0..65536 bytes"};

    std::string source(static_cast<std::size_t>(length), '\0');
    input.seekg(0);
    if (!input.read(source.data(), static_cast<std::streamsize>(source.size()))) return std::unexpected{"Cannot read client configuration"};

    const char*                   cursor = source.data();
    SettingsDetail::DocumentCheck document;
    if (!document.Members(cursor, cursor + source.size()))
      return std::unexpected{"Invalid client TOML: malformed document or duplicate key/table"};

    SettingsDetail::SettingsFile file;
    if (auto error = glz::read_toml(file, source); !source.empty() && error)
      return std::unexpected{"Invalid client TOML: " + glz::format_error(error, source)};
    if (file.version != ClientSettingsVersion) return std::unexpected{"Unsupported client configuration version"};
    if (file.serverIp.find('\0') != std::string::npos) return std::unexpected{"Invalid serverIp"};
    auto address = DreamNetAddress::TryParseIp(file.serverIp, file.serverPort);
    if (!address) return std::unexpected{"Invalid serverIp; expected an IPv4 address"};

    file.client.serverAddress = *address;
    const auto& view          = file.interpolation;
    file.client.movement =
      {std::chrono::milliseconds{view.delayMs}, std::chrono::milliseconds{view.maxGapMs}, view.historyCapacity, view.teleportDistance};
    return ClientSettings{
        std::move(file.client),
        std::move(file.authUrl),
        file.commandCapacity,
        file.stateCapacity,
        file.allowInsecureRemoteAuth
    };
  }

}
