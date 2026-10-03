module;
#include <glaze/toml.hpp>

export module Dreamsleeve.Client.Settings;

import std;
import Dreamsleeve.Client.Auth;
import Dreamsleeve.Client.Utils;
import Dreamsleeve.Client.Exchange;
import DreamNet.Client;
export import Dreamsleeve.Client.Config;

export namespace Dreamsleeve::Client
{

  constexpr int ClientSettingsVersion = 1;

  // A way to the server: its game port and its authentication origin. The main
  // route is the top of client.toml; [[routes]] add others, such as a proxy of
  // the server for players who cannot reach it directly.
  struct ConnectionRoute
  {
    std::string name;
    std::string serverHost;
    Port        serverPort{DefaultServerPort};
    std::string authUrl;

    bool operator==(const ConnectionRoute&) const = default;
  };

  // The main route's name; the others name themselves.
  constexpr std::string_view MainRouteName = "Основной";
  // Routes with the main one, and the bytes of a route's name.
  constexpr std::size_t MaxRoutes         = 8;
  constexpr std::size_t MaxRouteNameBytes = 64;

  struct ClientSettings
  {
    Configuration client{};
    std::string   authUrl{"http://127.0.0.1:8779"};
    std::size_t   commandCapacity{8};
    std::size_t   stateCapacity{8};
    bool          allowInsecureRemoteAuth{false};
    // The other routes, tried in this order after the main one.
    std::vector<ConnectionRoute> routes;
  };

  // The main route first, then the others. The saved login, the device and
  // the names book stay keyed by the main route whichever one carries the traffic.
  std::vector<ConnectionRoute> RoutesOf(const ClientSettings& settings)
  {
    std::vector<ConnectionRoute> routes{
        {std::string{MainRouteName}, settings.client.serverHost, settings.client.serverPort, settings.authUrl}
    };
    routes.insert(routes.end(), settings.routes.begin(), settings.routes.end());
    return routes;
  }

  // The position of the named route in RoutesOf, if there is one.
  std::optional<std::size_t> RouteIndex(std::span<const ConnectionRoute> routes, std::string_view name)
  {
    const auto found = std::ranges::find(routes, name, &ConnectionRoute::name);
    if (found == routes.end()) return std::nullopt;
    return static_cast<std::size_t>(found - routes.begin());
  }

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

      // [[name]] tables: the elements so far of each array.
      std::map<std::vector<std::string>, std::size_t> arrays;

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
          // [[name]]: one more element of an array of tables.
          const bool element = table && it != end && *it == '[';
          if (element) ++it;

          std::vector<std::string> path;
          if (!glz::parse_toml_key(path, context, it, end)) return false;

          if (element)
          {
            if (it == end || *it++ != ']' || it == end || *it++ != ']' || tables.contains(path) || keys.contains(path)) return false;
            // Each element is its own table: its keys never meet the previous one's.
            const auto index = arrays[path]++;
            scope            = std::move(path);
            scope.push_back("[" + std::to_string(index) + "]");
          }
          else if (table)
          {
            if (it == end || *it++ != ']' || !tables.insert(path).second || keys.contains(path) || arrays.contains(path)) return false;
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

    // The file shape: chrono values in milliseconds, the server as host and port.
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
      std::string       serverHost{Configuration{}.serverHost};
      Port              serverPort{Configuration{}.serverPort};
      std::string       authUrl{ClientSettings{}.authUrl};
      Configuration     client{};
      InterpolationFile interpolation{};
      std::size_t       commandCapacity{ClientSettings{}.commandCapacity};
      std::size_t       stateCapacity{ClientSettings{}.stateCapacity};
      bool              allowInsecureRemoteAuth{ClientSettings{}.allowInsecureRemoteAuth};
      std::vector<ConnectionRoute> routes;
    };

    // The first route setting that fails, named as in the file.
    std::optional<std::string> InvalidRoute(const ClientSettings& settings)
    {
      if (settings.routes.size() >= MaxRoutes) return "routes";
      std::set<std::string_view> names{MainRouteName};
      for (std::size_t index = 0; index < settings.routes.size(); ++index)
      {
        const auto& route = settings.routes[index];
        const auto  field = [&](std::string_view key) { return std::format("routes[{}].{}", index + 1, key); };
        const bool  control =
          std::ranges::any_of(route.name, [](char value) { return static_cast<unsigned char>(value) < 0x20 || value == 0x7F; });
        if (route.name.empty() || route.name.size() > MaxRouteNameBytes || control || !names.insert(route.name).second) return field("name");
        if (!DreamNetAddress::IsHostSyntax(route.serverHost)) return field("serverHost");
        if (route.serverPort == 0) return field("serverPort");
        if (!Auth::ValidateUrl(route.authUrl, settings.allowInsecureRemoteAuth)) return field("authUrl");
      }
      return std::nullopt;
    }

  }

  // The one check of every client setting; ClientApplication::TryCreate runs it,
  // and the runtime, codec and movement view trust what passed.
  export std::expected<void, std::string> ValidateClientSettings(const ClientSettings& settings)
  {
    const auto& client = settings.client;
    if (auto field = client.InvalidSetting()) return std::unexpected{"Invalid client setting: " + std::string{*field}};
    if (auto field = ClientExchange::InvalidCapacity(settings.commandCapacity, settings.stateCapacity))
      return std::unexpected{"Invalid client setting: " + std::string{*field}};
    if (auto field = SettingsDetail::InvalidRoute(settings)) return std::unexpected{"Invalid client setting: " + *field};
    // The host is resolved per connection; the transport check covers the ENet host and the timeouts.
    if (
      auto transport = DreamNetClient::ValidateConfig(
        {client.network, DreamNetAddress::Loopback(client.serverPort), client.connectTimeoutMs, client.disconnectTimeoutMs});
      !transport)
      return std::unexpected{"Invalid client network setting: " + transport.error().message};
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
    source = Dreamsleeve::Utils::Toml::OneLineArrays(source);

    const char*                   cursor = source.data();
    SettingsDetail::DocumentCheck document;
    if (!document.Members(cursor, cursor + source.size()))
      return std::unexpected{"Invalid client TOML: malformed document or duplicate key/table"};

    SettingsDetail::SettingsFile file;
    if (auto error = glz::read_toml(file, source); !source.empty() && error)
      return std::unexpected{"Invalid client TOML: " + glz::format_error(error, source)};
    if (file.version != ClientSettingsVersion) return std::unexpected{"Unsupported client configuration version"};

    file.client.serverHost = std::move(file.serverHost);
    file.client.serverPort = file.serverPort;
    const auto& view       = file.interpolation;
    file.client.movement =
      {std::chrono::milliseconds{view.delayMs}, std::chrono::milliseconds{view.maxGapMs}, view.historyCapacity, view.teleportDistance};
    return ClientSettings{
        std::move(file.client),
        std::move(file.authUrl),
        file.commandCapacity,
        file.stateCapacity,
        file.allowInsecureRemoteAuth,
        std::move(file.routes)
    };
  }

}
