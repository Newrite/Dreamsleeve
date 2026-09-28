module;
#define TOML_EXCEPTIONS 0
#include <toml++/toml.hpp>
#include <glaze/core/reflect.hpp>

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

    // Reuse the field metadata, without serializing an intermediate JSON document.
    template <class T>
    std::expected<void, std::string> Read(const toml::node& node, T& value, const std::string& path)
    {
      if constexpr (std::is_same_v<T, std::string> || std::is_same_v<T, bool>)
      {
        if (auto parsed = node.value<T>())
        {
          value = std::move(*parsed);
          return {};
        }
      }
      else if constexpr (std::is_integral_v<T>)
      {
        if (node.is_integer())
        {
          if (auto parsed = node.value<std::int64_t>(); parsed && std::in_range<T>(*parsed))
          {
            value = static_cast<T>(*parsed);
            return {};
          }
        }
      }
      else if constexpr (std::is_floating_point_v<T>)
      {
        if (node.is_integer() || node.is_floating_point())
        {
          if (auto parsed = node.value<double>(); parsed && std::isfinite(*parsed) && std::abs(*parsed) <= std::numeric_limits<T>::max())
          {
            value = static_cast<T>(*parsed);
            return {};
          }
        }
      }
      else
      {
        const auto* table = node.as_table();
        if (!table) return std::unexpected{"Expected TOML table: " + path};

        for (const auto& [key, item] : *table)
        {
          if (std::ranges::find(glz::reflect<T>::keys, key.str()) == std::ranges::end(glz::reflect<T>::keys))
            return std::unexpected{"Unknown setting: " + path + std::string{key.str()}};
        }

        std::expected<void, std::string> result;
        std::size_t                      index{};
        glz::for_each_field(value, [&](auto& field) {
          const auto key = glz::reflect<T>::keys[index++];
          if (const auto* child = table->get(key); child && result) result = Read(*child, field, path + std::string{key} + ".");
        });
        return result;
      }

      return std::unexpected{"Invalid setting type or numeric range: " + path};
    }

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

    SettingsDetail::SettingsFile file;
    auto                         parsed = toml::parse(source);
    if (!parsed) return std::unexpected{"Invalid client TOML: " + std::string{parsed.error().description()}};
    if (auto loaded = SettingsDetail::Read(parsed.table(), file, ""); !loaded) return std::unexpected{loaded.error()};
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
