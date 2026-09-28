export module Dreamsleeve.Client.ProtocolCodec;

import std;
export import Dreamsleeve.Client.Exchange;
export import Dreamsleeve.Client.Config;
export import DreamNet.Packet;

export namespace Dreamsleeve::Client::Wire
{

  inline constexpr std::uint32_t Version = 6;

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

  using ClientRequest = std::variant<OpenSession, SendChat, UpdatePlayer>;

  struct SessionOpened
  {
    std::uint64_t                    requestId;
    Domain::PlayerId                 selfPlayerId;
    Domain::ChatChannelId            globalChannelId;
    std::vector<Domain::Player>      players;
    std::vector<Domain::ChatMessage> recentMessages;
    std::string                     serverName;
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
      return std::holds_alternative<SendChat>(request) ? Channel::Chat : Channel::Control;
    }

private:

    explicit ProtocolCodec(Configuration config) : config(std::move(config)) {}

    Configuration config;
  };

}
