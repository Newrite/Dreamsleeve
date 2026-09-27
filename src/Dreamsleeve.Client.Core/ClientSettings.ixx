export module Dreamsleeve.Client.Settings;

import std;
export import Dreamsleeve.Client.Config;

export namespace Dreamsleeve::Client
{
  struct ClientSettings
  {
    Configuration client{};
    std::string authUrl{"http://127.0.0.1:8779"};
    std::size_t commandCapacity{8};
    std::size_t stateCapacity{8};
  };

  // The caller chooses the path. Missing/invalid files never silently use defaults.
  std::expected<ClientSettings, std::string> LoadClientSettings(const std::filesystem::path& path);
  std::expected<void, std::string> ValidateClientSettings(const ClientSettings& settings);
}
