module;
#include <glaze/glaze.hpp>
#include "AuthHttp.h"

module Dreamsleeve.Client.Settings;

// Address and chrono values have explicit file representations; protocol/domain
// objects retain their existing types and never depend on the JSON library.
template<> struct glz::meta<Dreamsleeve::Client::Configuration>
{
  using T = Dreamsleeve::Client::Configuration;
  static constexpr auto value = object(
    "network", &T::network, "connectTimeoutMs", &T::connectTimeoutMs,
    "disconnectTimeoutMs", &T::disconnectTimeoutMs, "sessionTimeoutMs", &T::sessionTimeoutMs,
    "maxInitialPlayers", &T::maxInitialPlayers, "maxRecentMessages", &T::maxRecentMessages,
    "chatCapacity", &T::chatCapacity, "maxPendingChatRequests", &T::maxPendingChatRequests,
    "maxPendingPlayerUpdates", &T::maxPendingPlayerUpdates, "maxActorValues", &T::maxActorValues,
    "playerSampleIntervalMs", &T::playerSampleIntervalMs, "visibilityDistance", &T::visibilityDistance,
    "showFireflies", &T::showFireflies, "maxPendingMovementSamples", &T::maxPendingMovementSamples);
};

namespace Dreamsleeve::Client
{
  namespace SettingsDetail
  {
    struct JsonOptions : glz::opts
    {
      bool validate_trailing_whitespace = true;
    };

    struct InterpolationFile
    {
      std::int64_t delayMs{MovementSettings{}.delay.count()};
      std::int64_t maxGapMs{MovementSettings{}.maxGap.count()};
      std::size_t historyCapacity{MovementSettings{}.historyCapacity};
      double teleportDistance{MovementSettings{}.teleportDistance};
    };

    struct SettingsFile
    {
      int version{1};
      std::string serverIp{"127.0.0.1"};
      std::uint16_t serverPort{8778};
      std::string authUrl{ClientSettings{}.authUrl};
      Configuration client{};
      InterpolationFile interpolation{};
      std::size_t commandCapacity{ClientSettings{}.commandCapacity};
      std::size_t stateCapacity{ClientSettings{}.stateCapacity};
    };
  }

  std::expected<void, std::string> ValidateClientSettings(const ClientSettings& settings)
  {
    if (auto field = settings.client.InvalidSetting())
      return std::unexpected{"Invalid client setting: " + std::string{*field}};
    if (settings.commandCapacity == 0 || settings.stateCapacity == 0)
      return std::unexpected{"Client exchange capacities must be positive"};
    return Auth::ValidateUrl(settings.authUrl);
  }

  std::expected<ClientSettings, std::string> LoadClientSettings(const std::filesystem::path& path)
  {
    std::ifstream input{path, std::ios::binary | std::ios::ate};
    if (!input) return std::unexpected{"Cannot open client configuration"};
    const auto length = input.tellg();
    if (length <= 0 || length > 65536) return std::unexpected{"Client configuration must contain 1..65536 bytes"};

    std::string json(static_cast<std::size_t>(length), '\0');
    input.seekg(0);
    if (!input.read(json.data(), static_cast<std::streamsize>(json.size())))
      return std::unexpected{"Cannot read client configuration"};

    SettingsDetail::SettingsFile file;
    if (glz::read<SettingsDetail::JsonOptions{}>(file, json))
      return std::unexpected{"Invalid client JSON: syntax, field type or unknown field"};
    if (file.version != 1) return std::unexpected{"Unsupported client configuration version"};
    if (file.serverIp.find('\0') != std::string::npos) return std::unexpected{"Invalid serverIp"};
    auto address = DreamNetAddress::TryParseIp(file.serverIp, file.serverPort);
    if (!address) return std::unexpected{"Invalid serverIp; expected an IPv4 address"};

    file.client.serverAddress = *address;
    const auto& view = file.interpolation;
    file.client.movement = {std::chrono::milliseconds{view.delayMs}, std::chrono::milliseconds{view.maxGapMs},
                           view.historyCapacity, view.teleportDistance};
    ClientSettings result{std::move(file.client), std::move(file.authUrl), file.commandCapacity, file.stateCapacity};
    if (auto valid = ValidateClientSettings(result); !valid) return std::unexpected{valid.error()};
    return result;
  }
}
