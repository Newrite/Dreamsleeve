export module Dreamsleeve.Client.Config;

import std;
import Dreamsleeve.Client.ProtocolChannels;
export import DreamNet.Host;
export import DreamNet.Address;
export import DreamNet.Core;

export namespace Dreamsleeve::Client
{

  // Protobuf sizes and counts are int; larger limits could never be met.
  constexpr std::size_t MaxProtobufCount = static_cast<std::size_t>(std::numeric_limits<int>::max());
  // A plugin-local form ID: the low 24 bits, without the load-order byte.
  constexpr std::uint32_t MaxLocalFormId = 0xFFFFFF;
  // Reference scale of a placed static; the engine keeps two decimals.
  constexpr float MinFormScale = 0.01f;
  constexpr float MaxFormScale = 10.0f;
  // One connection to one server, and at least the control, chat and movement channels.
  constexpr std::size_t ClientPeers        = 1;
  constexpr std::size_t MinChannels        = Wire::ChannelCount;
  constexpr auto        MaxMovementGap     = std::chrono::hours{1};
  constexpr std::size_t MinMovementHistory = 2;
  constexpr Port        DefaultServerPort  = 8778;

  struct MovementSettings
  {
    std::chrono::milliseconds delay{150};
    std::chrono::milliseconds maxGap{1000};
    std::size_t               historyCapacity{32};
    double                    teleportDistance{2048.0};
  };

  // Load externally, validate once with ValidateClientSettings (ClientApplication does),
  // then keep fixed for the network owner's lifetime; its parts trust the values.
  struct Configuration
  {
    NetConfig   network{[] {
      auto value         = NetConfig::Default();
      value.maxPeers     = ClientPeers;
      value.channelLimit = MinChannels;
      // About three seconds of the densest view (512 players at 20 Hz) while the
      // network thread is held up; ENet alone sets 256 KiB.
      value.receiveBufferBytes = 1024 * 1024;
      return value;
    }()};
    std::size_t maxInitialPlayers{4096};
    std::size_t maxRecentMessages{512};

    // An IPv4 literal or a DNS name, resolved again on every connection attempt.
    std::string serverHost{DreamNetAddress::LoopbackIp};
    Port        serverPort{DefaultServerPort};
    TimeOutMs   connectTimeoutMs{5000};
    TimeOutMs   disconnectTimeoutMs{2000};
    TimeOutMs   sessionTimeoutMs{5000};

    std::size_t chatCapacity{512};
    std::size_t maxPendingChatRequests{32};
    std::size_t maxPendingPlayerUpdates{32};
    std::size_t maxActorValues{64};
    TimeOutMs   playerSampleIntervalMs{100};

    // Local game view; it does not change server subscriptions.
    double        visibilityDistance{8192.0};
    bool          showFireflies{true};
    std::string   fireflyPlugin{"Skyrim.esm"};
    std::uint32_t fireflyFormId{0x02EB0F};
    float         fireflyScale{0.25f};

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
    std::size_t      maxPendingMovementSamples{16384};

    std::string      phantomCacheDirectory{"phantom-cache"};
    bool             phantomDiagnostics{false};

    // The first invalid setting of the client itself, named as in client.toml;
    // the ENet bounds of network and the timeouts are DreamNetClient::ValidateConfig's.
    std::optional<std::string_view> InvalidSetting() const noexcept
    {
      const auto plugin = [](std::string_view name) {
        return !name.empty() && name.find_first_of(std::string_view{"/\\:\0", 4}) == std::string_view::npos;
      };
      const auto formId = [](std::uint32_t id) {
        return id != 0 && id <= MaxLocalFormId;
      };
      const auto scale = [](float value) {
        return std::isfinite(value) && value >= MinFormScale && value <= MaxFormScale;
      };
      const auto count = [](std::size_t value) {
        return value != 0 && value <= MaxProtobufCount;
      };

      if (network.channelLimit < MinChannels) return "network.channelLimit";
      if (network.maxPacketBytes > MaxProtobufCount) return "network.maxPacketBytes";
      if (!DreamNetAddress::IsHostSyntax(serverHost)) return "serverHost";
      if (serverPort == 0) return "serverPort";

      if (!count(maxInitialPlayers)) return "maxInitialPlayers";
      if (maxRecentMessages > MaxProtobufCount) return "maxRecentMessages";
      if (!count(maxActorValues)) return "maxActorValues";
      if (chatCapacity == 0) return "chatCapacity";
      if (maxPendingChatRequests == 0) return "maxPendingChatRequests";
      if (maxPendingPlayerUpdates == 0) return "maxPendingPlayerUpdates";
      if (playerSampleIntervalMs == 0) return "playerSampleIntervalMs";
      if (sessionTimeoutMs == 0) return "sessionTimeoutMs";

      if (!std::isfinite(visibilityDistance) || visibilityDistance < 0) return "visibilityDistance";
      if (!plugin(fireflyPlugin)) return "fireflyPlugin";
      if (!formId(fireflyFormId)) return "fireflyFormId";
      if (!scale(fireflyScale)) return "fireflyScale";

      if (!plugin(groundNotePlugin)) return "groundNotePlugin";
      if (!formId(groundNoteFormId)) return "groundNoteFormId";
      if (!scale(groundNoteScale)) return "groundNoteScale";

      if (!plugin(deathMarkPlugin)) return "deathMarkPlugin";
      if (!formId(deathMarkFormId)) return "deathMarkFormId";
      if (!scale(deathMarkScale)) return "deathMarkScale";

      if (maxPendingMovementSamples == 0) return "maxPendingMovementSamples";
      if (phantomCacheDirectory.size() > 32760 || phantomCacheDirectory.find('\0') != std::string::npos) return "phantomCacheDirectory";
      if (movement.delay.count() < 0) return "interpolation.delayMs";
      if (movement.maxGap <= movement.delay || movement.maxGap > MaxMovementGap) return "interpolation.maxGapMs";
      if (movement.historyCapacity < MinMovementHistory) return "interpolation.historyCapacity";
      if (!std::isfinite(movement.teleportDistance) || movement.teleportDistance <= 0) return "interpolation.teleportDistance";

      return std::nullopt;
    }
  };

}
