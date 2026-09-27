export module Dreamsleeve.Client.Config;

import std;
export import DreamNet.Host;
export import DreamNet.Address;
export import DreamNet.Core;

export namespace Dreamsleeve::Client
{

  struct MovementSettings
  {
    std::chrono::milliseconds delay{150};
    std::chrono::milliseconds maxGap{1000};
    std::size_t               historyCapacity{32};
    double                    teleportDistance{2048.0};

    bool Valid() const noexcept
    {
      return delay.count() >= 0 && maxGap > delay && maxGap <= std::chrono::hours{1} && historyCapacity >= 2 &&
             std::isfinite(teleportDistance) && teleportDistance > 0;
    }
  };

  // Load externally before creating the network owner; keep fixed for its lifetime.
  struct Configuration
  {
    NetConfig       network{[] {
      auto value         = NetConfig::Default();
      value.maxPeers     = 1;
      value.channelLimit = 3;
      return value;
    }()};
    std::size_t     maxInitialPlayers{4096};
    std::size_t     maxRecentMessages{512};
    DreamNetAddress serverAddress{DreamNetAddress::Loopback(8778)};
    TimeOutMs       connectTimeoutMs{5000};
    TimeOutMs       disconnectTimeoutMs{2000};
    TimeOutMs       sessionTimeoutMs{5000};
    std::size_t     chatCapacity{512};
    std::size_t     maxPendingChatRequests{32};
    std::size_t     maxPendingPlayerUpdates{32};
    std::size_t     maxActorValues{64};
    TimeOutMs       playerSampleIntervalMs{50};
    // Future game-view preferences. They do not change server subscriptions.
    double           visibilityDistance{8192.0};
    bool             showFireflies{true};
    MovementSettings movement{};
    std::size_t      maxPendingMovementSamples{4096};

    std::optional<std::string_view> InvalidSetting() const noexcept
    {
      if (auto field = InvalidProtocolSetting()) return field;
      if (network.maxPeers != 1) return "network.maxPeers";
      if (network.channelLimit > 255) return "network.channelLimit";
      if (serverAddress.GetPort() == 0) return "serverAddress.port";
      if (chatCapacity == 0) return "chatCapacity";
      if (maxPendingChatRequests == 0) return "maxPendingChatRequests";
      if (maxPendingPlayerUpdates == 0) return "maxPendingPlayerUpdates";
      if (playerSampleIntervalMs == 0) return "playerSampleIntervalMs";
      if (sessionTimeoutMs == 0) return "sessionTimeoutMs";
      if (connectTimeoutMs == 0) return "connectTimeoutMs";
      if (disconnectTimeoutMs == 0) return "disconnectTimeoutMs";
      if (!movement.Valid()) return "movement";
      if (maxPendingMovementSamples == 0) return "maxPendingMovementSamples";
      if (!std::isfinite(visibilityDistance) || visibilityDistance < 0) return "visibilityDistance";
      return std::nullopt;
    }

    std::optional<std::string_view> InvalidProtocolSetting() const noexcept
    {
      if (network.channelLimit < 3) return "channelLimit";
      if (network.maxPacketBytes == 0 || network.maxPacketBytes > static_cast<std::size_t>(std::numeric_limits<int>::max()))
        return "maxPacketBytes";
      if (network.maxWaitingData < network.maxPacketBytes) return "maxWaitingData";
      if (maxInitialPlayers == 0 || maxInitialPlayers > static_cast<std::size_t>(std::numeric_limits<int>::max()))
        return "maxInitialPlayers";
      if (maxActorValues == 0 || maxActorValues > static_cast<std::size_t>(std::numeric_limits<int>::max())) return "maxActorValues";
      if (maxRecentMessages > static_cast<std::size_t>(std::numeric_limits<int>::max())) return "maxRecentMessages";
      return std::nullopt;
    }
  };

}
