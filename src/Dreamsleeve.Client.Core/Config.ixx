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
    double        visibilityDistance{8192.0};
    bool          showFireflies{true};
    std::string   fireflyPlugin{"Skyrim.esm"};
    std::uint32_t fireflyFormId{0x02EB0F};
    float         fireflyScale{0.25f};
    bool          showFireflyNames{true};
    bool          fireflyNameOcclusion{true};
    float         fireflyNameFontSize{18.0f};
    float         fireflyNameOffset{35.0f};
    // Withhold keyboard events from the game and other SKSE mods while the chat is open.
    bool captureKeyboard{true};
    // Ground mark visuals: STAT base forms without collision (plugin-local IDs).
    std::string      groundNotePlugin{"Skyrim.esm"};
    std::uint32_t    groundNoteFormId{0x075DDB};  // FXGlowFlatRndBrt
    float            groundNoteScale{0.5f};
    std::string      deathMarkPlugin{"Skyrim.esm"};
    std::uint32_t    deathMarkFormId{0x075DD9};  // FXGlowFlatRndDim
    float            deathMarkScale{0.5f};
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
      if (fireflyPlugin.empty() || fireflyPlugin.find_first_of("/\\:\0", 0, 4) != std::string::npos) return "fireflyPlugin";
      if (fireflyFormId == 0 || fireflyFormId > 0xFFFFFF) return "fireflyFormId";
      if (!std::isfinite(fireflyScale) || fireflyScale < 0.01f || fireflyScale > 10.0f) return "fireflyScale";
      if (!std::isfinite(fireflyNameFontSize) || fireflyNameFontSize < 8 || fireflyNameFontSize > 48) return "fireflyNameFontSize";
      if (!std::isfinite(fireflyNameOffset) || fireflyNameOffset < 0 || fireflyNameOffset > 512) return "fireflyNameOffset";
      if (groundNotePlugin.empty() || groundNotePlugin.find_first_of("/\\:\0", 0, 4) != std::string::npos) return "groundNotePlugin";
      if (groundNoteFormId == 0 || groundNoteFormId > 0xFFFFFF) return "groundNoteFormId";
      if (!std::isfinite(groundNoteScale) || groundNoteScale < 0.01f || groundNoteScale > 10.0f) return "groundNoteScale";
      if (deathMarkPlugin.empty() || deathMarkPlugin.find_first_of("/\\:\0", 0, 4) != std::string::npos) return "deathMarkPlugin";
      if (deathMarkFormId == 0 || deathMarkFormId > 0xFFFFFF) return "deathMarkFormId";
      if (!std::isfinite(deathMarkScale) || deathMarkScale < 0.01f || deathMarkScale > 10.0f) return "deathMarkScale";
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
