export module Dreamsleeve.Client.ProtocolCodec;

import std;
export import Dreamsleeve.Client.Exchange;
export import Dreamsleeve.Client.Config;
export import DreamNet.Packet;

export namespace Dreamsleeve::Client::Wire
{

  inline constexpr std::uint32_t Version = 4;

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

  using PlayerUpdate = std::variant<CharacterStarted, CharacterRenamed, LocalMovement, LocalActorValues, GameExited, PlayerDetailsChanged>;

  struct UpdatePlayer
  {
    std::uint64_t requestId{};
    PlayerUpdate update;
  };

  using ClientRequest = std::variant<OpenSession, SendChat, UpdatePlayer>;

  struct SessionOpened
  {
    std::uint64_t                    requestId;
    Domain::PlayerId                 selfPlayerId;
    Domain::ChatChannelId            globalChannelId;
    std::vector<Domain::Player>      players;
    std::vector<Domain::ChatMessage> recentMessages;
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

  // Replies carry required correlation; notifications have no request ID.
  // Own and broadcast chat both apply the same ChatMessagesReceived update.
  using ServerResponse = std::variant<SessionOpened, ChatAccepted, ChatMessagesReceived, ServerRejection, PlayerUpserted, PlayerRemoved, PlayerLocationUpdated, PlayerMetadataUpdated, PlayerUpdateAccepted>;

  // One immutable configuration per network owner. Validate once at startup.
  class ProtocolCodec
  {
public:

    static Result<ProtocolCodec> TryCreate(Configuration config);

    // Serializes directly into the owning ENet packet; send with PushPacket/Send.
    Result<DreamNetPacket> Encode(const ClientRequest& request) const;
    Result<ServerResponse> Decode(std::span<const std::byte> packet) const;

private:

    explicit ProtocolCodec(Configuration config) : config(std::move(config)) {}

    Configuration config;
  };

}
