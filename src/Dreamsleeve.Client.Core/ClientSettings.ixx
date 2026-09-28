module;
#include <glaze/toml.hpp>

export module Dreamsleeve.Client.Settings;

import std;
import Dreamsleeve.Client.Auth;
export import Dreamsleeve.Client.Config;

export namespace Dreamsleeve::Client
{

  struct ClientSettings
  {
    Configuration client{};
    std::string   authUrl{"http://127.0.0.1:8779"};
    std::size_t   commandCapacity{8};
    std::size_t   stateCapacity{8};
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

    struct InterpolationFile
    {
      std::int64_t delayMs{MovementSettings{}.delay.count()};
      std::int64_t maxGapMs{MovementSettings{}.maxGap.count()};
      std::size_t  historyCapacity{MovementSettings{}.historyCapacity};
      double       teleportDistance{MovementSettings{}.teleportDistance};
    };

    struct SettingsFile
    {
      int               version{1};
      std::string       serverIp{"127.0.0.1"};
      std::uint16_t     serverPort{8778};
      std::string       authUrl{ClientSettings{}.authUrl};
      Configuration     client{};
      InterpolationFile interpolation{};
      std::size_t       commandCapacity{ClientSettings{}.commandCapacity};
      std::size_t       stateCapacity{ClientSettings{}.stateCapacity};
    };

  }

  export std::expected<void, std::string> ValidateClientSettings(const ClientSettings& settings)
  {
    if (auto field = settings.client.InvalidSetting()) return std::unexpected{"Invalid client setting: " + std::string{*field}};
    if (settings.commandCapacity == 0 || settings.stateCapacity == 0) return std::unexpected{"Client exchange capacities must be positive"};
    return Auth::ValidateUrl(settings.authUrl);
  }

  // The caller chooses the path. Missing/invalid files never silently use defaults.
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
    if (file.version != 1) return std::unexpected{"Unsupported client configuration version"};
    if (file.serverIp.find('\0') != std::string::npos) return std::unexpected{"Invalid serverIp"};
    auto address = DreamNetAddress::TryParseIp(file.serverIp, file.serverPort);
    if (!address) return std::unexpected{"Invalid serverIp; expected an IPv4 address"};

    file.client.serverAddress = *address;
    const auto& view          = file.interpolation;
    file.client.movement =
      {std::chrono::milliseconds{view.delayMs}, std::chrono::milliseconds{view.maxGapMs}, view.historyCapacity, view.teleportDistance};
    ClientSettings result{std::move(file.client), std::move(file.authUrl), file.commandCapacity, file.stateCapacity};
    if (auto valid = ValidateClientSettings(result); !valid) return std::unexpected{valid.error()};
    return result;
  }

}
