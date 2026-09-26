export module Dreamsleeve.Client.Codec;

import std;
export import Dreamsleeve.Client.Exchange;
export import Dreamsleeve.Client.Config;
export import DreamNet.Packet;

export namespace Dreamsleeve::Client::Wire
{

  inline constexpr std::uint32_t Version = 1;

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
    std::string   username;
    std::string   displayName;
  };

  using ClientRequest = std::variant<OpenSession, SendChat>;

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

  // Replies carry required correlation; notifications have no request ID.
  // Own and broadcast chat both apply the same ChatMessagesReceived update.
  using ServerResponse = std::variant<SessionOpened, ChatAccepted, ChatMessagesReceived, ServerRejection, PlayerUpserted, PlayerRemoved>;

  // One immutable configuration per network owner. Validate once at startup.
  class Codec
  {
public:

    static Result<Codec> TryCreate(Configuration config);

    // Serializes directly into the owning ENet packet; send with PushPacket/Send.
    Result<DreamNetPacket> Encode(const ClientRequest& request) const;
    Result<ServerResponse> Decode(std::span<const std::byte> packet) const;

private:

    explicit Codec(Configuration config) : config(std::move(config)) {}

    Configuration config;
  };

}
