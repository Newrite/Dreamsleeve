export module Dreamsleeve.Client.ProtocolCodec;

import std;
export import Dreamsleeve.Client.Exchange;
export import Dreamsleeve.Client.Config;
export import DreamNet.Packet;

export namespace Dreamsleeve::Client::Wire
{

  inline constexpr std::uint32_t Version = 7;

  enum class ErrorCode
  {
    InvalidConfig,
    EmptyPacket,
    PacketTooLarge,
    MalformedPacket,
    UnsupportedVersion,
    InvalidEnvelope,
    InvalidPayload,
    PacketCreationFailed
  };

  struct Error
  {
    ErrorCode   code;
    std::string field;
  };
  template <class T>
  using Result = std::expected<T, Error>;

  struct OpenSession
  {
    std::uint64_t requestId{};
    std::string   sessionTicket;
  };

  enum class Channel : std::uint8_t
  {
    Control  = 0,
    Chat     = 1,
    Realtime = 2
  };

  struct SetLocation
  {
    std::uint64_t                         contextRevision;
    std::optional<Domain::PlayerLocation> location;
  };

  struct MovementSample
  {
    std::uint64_t        contextRevision;
    std::uint64_t        sequence;
    Domain::MovementPose pose;
  };

  using PlayerUpdate = std::variant<CharacterStarted, CharacterRenamed, SetLocation, LocalActorValues, GameExited, PlayerDetailsChanged>;

  struct UpdatePlayer
  {
    std::uint64_t requestId{};
    PlayerUpdate  update;
  };

  using ClientRequest = std::variant<OpenSession, SendChat, UpdatePlayer, PostAnnouncement>;

  // A channel of the session with its retained tail, ascending MessageId.
  struct ChannelOpened
  {
    Domain::ChatChannelId            channelId{};
    Domain::ChatChannelKind          kind{Domain::ChatChannelKind::Global};
    std::vector<Domain::ChatMessage> recentMessages;
  };

  struct SessionOpened
  {
    std::uint64_t               requestId;
    Domain::PlayerId            selfPlayerId;
    std::vector<Domain::Player> players;
    std::vector<ChannelOpened>  channels;
    std::string                 serverName;
    Domain::AnnouncementPolicy  announcements;
  };

  struct ChatAccepted
  {
    std::uint64_t        requestId;
    ChatMessagesReceived changes;
  };

  struct PlayerUpdateAccepted
  {
    std::uint64_t requestId;
  };

  struct PlayersMoved
  {
    std::vector<PlayerMovementReceived> players;
  };

  // Replies carry required correlation; notifications have no request ID.
  // Own and broadcast chat both apply the same ChatMessagesReceived update.
  using ServerResponse = std::variant<
    SessionOpened,
    ChatAccepted,
    ChatMessagesReceived,
    ServerRejection,
    PlayerUpserted,
    PlayerRemoved,
    PlayersMoved,
    PlayerMetadataUpdated,
    PlayerLocationUpdated,
    PlayerUpdateAccepted>;

  // One immutable configuration per network owner. Validate once at startup.
  class ProtocolCodec
  {
public:

    static Result<ProtocolCodec> TryCreate(Configuration config);

    // Serializes directly into the owning ENet packet; send with PushPacket/Send.
    Result<DreamNetPacket> Encode(const ClientRequest& request) const;
    Result<DreamNetPacket> Encode(const MovementSample& sample, std::size_t maxPayloadBytes) const;
    Result<ServerResponse> Decode(std::span<const std::byte> packet, Channel channel = Channel::Control) const;

    static Channel RequestChannel(const ClientRequest& request)
    {
      const bool chat = std::holds_alternative<SendChat>(request) || std::holds_alternative<PostAnnouncement>(request);
      return chat ? Channel::Chat : Channel::Control;
    }

private:

    explicit ProtocolCodec(Configuration config) : config(std::move(config)) {}

    Configuration config;
  };

}
