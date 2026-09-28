module;

#include "Prelude.hpp"

export module Dreamsleeve.Runtime;

import Dreamsleeve.Client.Application;
import Dreamsleeve.Client.Settings;

namespace Runtime
{
  namespace Dream = Dreamsleeve::Client;

  constexpr std::string_view settingPath = "Data/skse/plugins/DreamnetClientSettings.toml"sv;

  Dream::ClientApplication::Ptr GetClientApplication()
  {
    static Dream::ClientApplication::Ptr clientApplication = nullptr;

    if (clientApplication) return std::move(clientApplication);

    auto settings = Dream::LoadClientSettings(settingPath);
    if (!settings)
    {
      logger::error("Could not load Dream::LoadClientSettings from {} with error {}", settingPath, settings.error());
      return nullptr;
    }

    auto client = Dream::ClientApplication::TryCreate(*settings);
    if (!client)
    {
      logger::error("Could not create Dream::ClientApplication, error: {}", client.error());
      return nullptr;
    }

    clientApplication = std::move(*client);

    return std::move(clientApplication);
  }

}
