export module Dreamsleeve.Client.Config;

import std;
export import DreamNet.Host;
export import DreamNet.Address;
export import DreamNet.Core;

export namespace Dreamsleeve::Client
{

  // Load externally before creating the network owner; keep fixed for its lifetime.
  struct Configuration
  {
    NetConfig       network{[] {
      auto value     = NetConfig::Default();
      value.maxPeers = 1;
      return value;
    }()};
    std::size_t     maxInitialPlayers{4096};
    std::size_t     maxRecentMessages{512};
    DreamNetAddress serverAddress{DreamNetAddress::Loopback(8778)};
    TimeOutMs       connectTimeoutMs{5000};
    TimeOutMs       disconnectTimeoutMs{2000};
    TimeOutMs       sessionTimeoutMs{5000};
    std::size_t     chatCapacity{512};

    std::optional<std::string_view> InvalidProtocolSetting() const noexcept
    {
      if (network.maxPacketBytes == 0 || network.maxPacketBytes > static_cast<std::size_t>(std::numeric_limits<int>::max()))
        return "maxPacketBytes";
      if (network.maxWaitingData < network.maxPacketBytes) return "maxWaitingData";
      if (maxInitialPlayers == 0 || maxInitialPlayers > static_cast<std::size_t>(std::numeric_limits<int>::max()))
        return "maxInitialPlayers";
      if (maxRecentMessages > static_cast<std::size_t>(std::numeric_limits<int>::max())) return "maxRecentMessages";
      return std::nullopt;
    }
  };

}
