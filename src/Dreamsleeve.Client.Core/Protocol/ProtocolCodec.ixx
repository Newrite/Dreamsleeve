export module Dreamsleeve.Client.ProtocolCodec;

import std;
export import Dreamsleeve.Client.Exchange;
export import Dreamsleeve.Client.Config;
export import DreamNet.Packet;
export import Dreamsleeve.Client.ProtocolChannels;

export namespace Dreamsleeve::Client::Wire
{

  inline constexpr std::uint32_t Version = 23;

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
    // Where others see a server pseudonym, from the first packet.
    Domain::HiddenIdentity hiding{Domain::HiddenIdentity::None};
  };

  // Keeps the connection without a session: the server counts this client
  // online until it opens one on the same connection. No reply.
  struct JoinAsGuest
  {
    std::uint64_t requestId{};
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

  using ClientRequest = std::variant<
    OpenSession,
    JoinAsGuest,
    SendChat,
    UpdatePlayer,
    PostAnnouncement,
    PlaceGroundNote,
    ReportDeath,
    RemoveGroundMark,
    SetIdentityVisibility,
    ChangeDisplayName,
    SetNameColor,
    SanctionPlayer,
    LiftSanction,
    KickPlayer,
    ListSanctions,
    ListPlayerMarks,
    ClearPlayerMarks,
    DeleteChatMessage,
    GuildRequest>;

  // A server number for one key and label of actor values, defined to the
  // session before the first message that uses it.
  struct ActorValueKind
  {
    std::uint64_t          id{};
    Domain::ActorValueKey  key;
    Domain::ActorValueName displayName;
  };

  // The kinds one session knows. The server never reuses a number, and never
  // sends again a kind that no online player has, so Retain may forget those.
  class ActorValueKinds
  {
public:

    const ActorValueKind* Find(std::uint64_t id) const
    {
      const auto found = kinds.find(id);
      return found == kinds.end() ? nullptr : &found->second;
    }

    // False for a zero or already defined number.
    bool Define(ActorValueKind kind)
    {
      const auto id = kind.id;
      return id != 0 && kinds.try_emplace(id, std::move(kind)).second;
    }

    std::size_t Size() const noexcept
    {
      return kinds.size();
    }

    void Clear() noexcept
    {
      kinds.clear();
    }

    // Keeps the kinds some of these players still have.
    void Retain(std::span<const Domain::Player> players)
    {
      std::set<std::pair<std::string_view, std::string_view>> used;
      for (const auto& player : players)
        for (const auto& [key, info] : player.actorValues)
          used.emplace(key, info.displayName);
      std::erase_if(kinds, [&](const auto& entry) { return !used.contains({entry.second.key, entry.second.displayName}); });
    }

private:

    std::unordered_map<std::uint64_t, ActorValueKind> kinds;
  };

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
    // What the others see while this player's names are hidden; the self entry
    // in players keeps the real profile.
    std::optional<std::string> ownPseudonym;
    Domain::HiddenIdentity     hiding{Domain::HiddenIdentity::None};
    // The receiver's mute when the session opened.
    std::optional<Domain::MuteState> mute;
    Domain::PlayerRole               role{Domain::PlayerRole::Player};
    // Every kind the players use: the session's table starts from these.
    std::vector<ActorValueKind> kinds;
  };

  // Everything that changed in the online list in one server message: the
  // kinds it defines, then the model updates in their wire order.
  struct PresenceChanged
  {
    std::vector<ActorValueKind> kinds;
    std::vector<ClientUpdate>   updates;
  };

  // The receiver's role changed while the session is open.
  struct RoleChanged
  {
    Domain::PlayerRole role{Domain::PlayerRole::Player};
  };

  // The receiver's own mute changed; absent when lifted.
  struct MuteChanged
  {
    std::optional<Domain::MuteState> mute;
  };

  // The server ends the session; the connection closes right after.
  struct SessionEnded
  {
    Domain::SessionEnd end;
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

  // Acceptance of PlaceGroundNote or ReportDeath. The mark is also delivered
  // through GroundMarksChanged when it is visible from the author's position.
  struct GroundMarkPlaced
  {
    std::uint64_t                       requestId;
    Domain::GroundMark                  mark;
    std::optional<Domain::GroundMarkId> evictedId;
  };

  struct GroundMarkRemoved
  {
    std::uint64_t        requestId;
    Domain::GroundMarkId markId;
  };

  // Settles SetIdentityVisibility: where the names are hidden now and the
  // pseudonym the others see there; present exactly when hidden anywhere.
  struct IdentityVisibilityChanged
  {
    std::uint64_t              requestId;
    std::optional<std::string> pseudonym;
    Domain::HiddenIdentity     hiding{Domain::HiddenIdentity::None};
  };

  // Settles ChangeDisplayName with the stored name.
  struct DisplayNameChanged
  {
    std::uint64_t requestId;
    std::string   displayName;
  };

  // Settles SetNameColor with the stored color.
  struct NameColorChanged
  {
    std::uint64_t requestId;
    std::uint32_t nameColor;
  };

  // Moderator answers, each settling its request.
  struct SanctionIssued
  {
    std::uint64_t    requestId;
    Domain::Sanction sanction;
  };

  struct SanctionLifted
  {
    std::uint64_t        requestId;
    Domain::PlayerId     playerId;
    Domain::SanctionKind kind;
  };

  struct PlayerKicked
  {
    std::uint64_t    requestId;
    Domain::PlayerId playerId;
  };

  struct SanctionList
  {
    std::uint64_t                 requestId;
    std::vector<Domain::Sanction> sanctions;
  };

  struct PlayerMarks
  {
    std::uint64_t                   requestId;
    Domain::PlayerId                playerId;
    std::vector<Domain::GroundMark> marks;
  };

  struct PlayerMarksCleared
  {
    std::uint64_t    requestId;
    Domain::PlayerId playerId;
    std::uint32_t    removed;
  };

  // A message a moderator removed, on the chat lane; the request ID only on
  // the moderator's own copy.
  struct ChatMessageRemoved
  {
    std::optional<std::uint64_t> requestId;
    Domain::ChatChannelId        channelId;
    Domain::ChatMessageId        messageId;
  };

  struct RequestRejected
  {
    std::uint64_t   requestId;
    ServerRejection rejection;
  };

  // A guild with its channel's retained tail, ascending MessageId.
  struct GuildOpened
  {
    Domain::Guild                    guild;
    std::vector<Domain::ChatMessage> recentMessages;
  };

  // After SessionOpened: every guild and invitation of the player, replacing
  // what the client knew.
  struct GuildsSnapshot
  {
    std::vector<GuildOpened>         guilds;
    std::vector<Domain::GuildInvite> invites;
    Domain::GuildLimits              limits;
  };

  // The player created or joined it.
  struct GuildAdded
  {
    GuildOpened guild;
  };

  // The player left, was excluded, or it was disbanded.
  struct GuildRemoved
  {
    Domain::GuildId            guildId{};
    Domain::GuildRemovalReason reason{Domain::GuildRemovalReason::Left};
  };

  // A member joined, or their role, mute, online state or name changed.
  struct GuildMemberUpdated
  {
    Domain::GuildId     guildId{};
    Domain::GuildMember member;
  };

  struct GuildMemberRemoved
  {
    Domain::GuildId            guildId{};
    Domain::PlayerId           playerId{};
    Domain::GuildRemovalReason reason{Domain::GuildRemovalReason::Left};
  };

  struct GuildInvited
  {
    Domain::GuildInvite invite;
  };

  // Accepted, declined, expired or disbanded.
  struct GuildInviteRemoved
  {
    Domain::GuildId guildId{};
  };

  struct GuildChanged
  {
    std::variant<GuildAdded, GuildRemoved, GuildMemberUpdated, GuildMemberRemoved, GuildInvited, GuildInviteRemoved> change;
  };

  // Settles a GuildRequest; its effect arrived before as GuildChanged.
  struct GuildCommandDone
  {
    std::uint64_t   requestId;
    Domain::GuildId guildId;
  };

  // Replies carry required correlation; notifications have no request ID.
  // Own and broadcast chat both apply the same ChatMessagesReceived update.
  using ServerResponse = std::variant<
    SessionOpened,
    ChatAccepted,
    ChatMessagesReceived,
    RequestRejected,
    PresenceChanged,
    PlayersMoved,
    PlayerUpdateAccepted,
    GroundMarksChanged,
    GroundMarkPlaced,
    GroundMarkRemoved,
    OwnGroundMarksReplaced,
    IdentityVisibilityChanged,
    DisplayNameChanged,
    NameColorChanged,
    MuteChanged,
    SessionEnded,
    RoleChanged,
    SanctionIssued,
    SanctionLifted,
    PlayerKicked,
    SanctionList,
    PlayerMarks,
    PlayerMarksCleared,
    ChatMessageRemoved,
    GuildsSnapshot,
    GuildChanged,
    GuildCommandDone>;

  // One immutable configuration per network owner, checked by ValidateClientSettings.
  class ProtocolCodec
  {
public:

    explicit ProtocolCodec(Configuration config) : config(std::move(config)) {}

    // Serializes directly into the owning ENet packet; send with PushPacket/Send.
    Result<DreamNetPacket> Encode(const ClientRequest& request) const;
    Result<DreamNetPacket> Encode(const MovementSample& sample, std::size_t maxPayloadBytes) const;
    // Presence resolves actor value numbers through the session's kinds.
    Result<ServerResponse> Decode(
      std::span<const std::byte> packet,
      Channel                    channel = Channel::Control,
      const ActorValueKinds&     kinds   = ActorValueKinds{}) const;

private:

    Configuration config;
  };

}
