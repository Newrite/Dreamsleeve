export module Dreamsleeve.Host.BridgeProjection;

import std;
import Dreamsleeve.Client.Exchange;
import Dreamsleeve.Client.Utils;
import Dreamsleeve.Host.Bridge;
import Dreamsleeve.Host.BridgeTexts;
import Dreamsleeve.Host.GameDates;
export import Dreamsleeve.Host.Names;

// Domain values as the contract's types (Bridge), and the page's commands as
// client actions: naming, the local text filter and streamer mode apply here.
namespace Dreamsleeve::Host::Bridge
{

  std::optional<std::string> Text(const std::string& value)
  {
    if (value.empty()) return std::nullopt;
    return value;
  }

  std::optional<std::string> Label(std::string_view value)
  {
    if (value.empty()) return std::nullopt;
    return std::string{value};
  }

}

export namespace Dreamsleeve::Host::Bridge
{

  UiPlayer ToUiAuthor(
    const Domain::PlayerData&                   data,
    const std::optional<Domain::CharacterName>& character,
    bool                                        inCharacter,
    Names&                                      names,
    const UiSettings&                           settings)
  {
    UiPlayer player;
    player.id           = Id(data.playerId);
    player.name         = names.NameFor(data.playerId, data, character, settings);
    player.inCharacter  = inCharacter;
    player.pseudonymous = data.pseudonymous;
    if (data.nameColor) player.color = ColorText(*data.nameColor);
    if (settings.streamerMode || data.pseudonymous)
    {
      if (settings.streamerMode) player.alias = player.name;
      player.displayName = player.name;
      return player;
    }
    player.displayName = data.displayName;
    player.username    = data.username;
    player.character   = character;
    return player;
  }

  // The UI description of a channel entity; the UI's "all" view is its own
  // aggregate. A guild's channel is named after the guild.
  UiChannel ToUiChannel(Domain::ChatChannelId id, Domain::ChatChannelKind kind, std::string_view guild = {})
  {
    if (kind == Domain::ChatChannelKind::System) return {Id(id), std::string{ChannelKindNames[2]}, "Объявления", false};
    if (kind == Domain::ChatChannelKind::Guild)
      return {Id(id), std::string{ChannelKindNames[1]}, guild.empty() ? std::string{"Гильдия"} : std::string{guild}, true};
    return {Id(id), std::string{ChannelKindNames[0]}, "Общий", true};
  }

  std::string_view GuildRoleName(Domain::GuildRole role)
  {
    return NameOf(GuildRoleNames, role, Domain::GuildRole::Member, GuildRoleNames.front());
  }

  std::string_view GuildRemovalName(Domain::GuildRemovalReason reason)
  {
    return NameOf(GuildRemovalNames, reason, Domain::GuildRemovalReason::Left, GuildRemovalNames.front());
  }

  std::string_view GuildApplicationEndName(Domain::GuildApplicationEnd reason)
  {
    return NameOf(GuildApplicationEndNames, reason, Domain::GuildApplicationEnd::Withdrawn, GuildApplicationEndNames.front());
  }

  // The page's query; the bridge admitted the sort name.
  Domain::GuildCatalogQuery GuildCatalogQueryOf(Commands::GuildCatalog& command)
  {
    Domain::GuildCatalogQuery query{
        .search     = std::move(command.search),
        .minMembers = command.minMembers,
        .maxMembers = command.maxMembers,
        .openOnly   = command.openOnly,
        .sort       = command.sort == GuildCatalogSortNames[1] ? Domain::GuildCatalogSort::Members : Domain::GuildCatalogSort::Name,
    };
    if (command.after)
      query.after = Domain::GuildCatalogAfter{command.after->guildId.value, std::move(command.after->name), command.after->members};
    return query;
  }

  std::vector<UiCatalogGuild> ToUiCatalog(const Domain::GuildCatalogPage& page)
  {
    std::vector<UiCatalogGuild> result;
    result.reserve(page.guilds.size());
    for (const auto& entry : page.guilds)
      result.push_back({Id(entry.guildId), entry.name, entry.info, entry.members, entry.createdAtUnixMs});
    return result;
  }

  // Named like every other surface: the real profile, or the alias in streamer mode.
  UiGuildMember ToUiGuildMember(const Domain::GuildMember& member, Names& names, const UiSettings& settings)
  {
    UiGuildMember result{
        .id       = Id(member.profile.playerId),
        .name     = names.NameFor(member.profile.playerId, member.profile, std::nullopt, settings),
        .role     = std::string{GuildRoleName(member.role)},
        .online   = member.online,
        .joinedAt = member.joinedAtUnixMs,
    };
    if (member.mute) result.mute = UiGuildMute{member.mute->reason, member.mute->untilUnixMs};
    if (member.titleId) result.title = Id(*member.titleId);
    return result;
  }

  UiGuild ToUiGuild(const Domain::Guild& guild, Names& names, const UiSettings& settings)
  {
    UiGuild result{Id(guild.guildId), guild.name, Id(guild.channelId), guild.createdAtUnixMs, {}, {}, guild.info, guild.listed, {}};
    for (const auto& member : guild.members)
      result.members.push_back(ToUiGuildMember(member, names, settings));
    for (const auto& title : guild.titles)
      result.titles.push_back({Id(title.titleId), title.name});
    for (const auto& request : guild.joinRequests)
      result.joinRequests.push_back(
        {Id(request.profile.playerId),
         names.NameFor(request.profile.playerId, request.profile, std::nullopt, settings),
         request.createdAtUnixMs,
         request.expiresAtUnixMs});
    return result;
  }

  // The values of the command its action uses; the rest are ignored.
  Dreamsleeve::Client::GuildAction GuildActionOf(Commands::Guild& command)
  {
    namespace Client   = Dreamsleeve::Client;
    const auto  guild  = command.guildId.value;
    const auto  player = command.playerId.value;
    const auto& action = command.action;
    if (action == "invite") return Client::InviteToGuild{guild, player};
    if (action == "answer") return Client::AnswerGuildInvite{guild, command.accept};
    if (action == "leave") return Client::LeaveGuild{guild};
    if (action == "exclude") return Client::ExcludeGuildMember{guild, player};
    if (action == "setRole")
      return Client::SetGuildRole{
          guild,
          player,
          command.role == GuildRoleNames[1] ? Domain::GuildRole::Officer : Domain::GuildRole::Member
      };
    if (action == "transfer") return Client::TransferGuild{guild, player};
    if (action == "mute") return Client::MuteGuildMember{guild, player, command.minutes, std::move(command.reason)};
    if (action == "unmute") return Client::UnmuteGuildMember{guild, player};
    if (action == "disband") return Client::DisbandGuild{guild};
    const auto title = command.titleId.value;
    if (action == "createTitle") return Client::CreateGuildTitle{guild, std::move(command.name)};
    if (action == "renameTitle") return Client::RenameGuildTitle{guild, title, std::move(command.name)};
    if (action == "deleteTitle") return Client::DeleteGuildTitle{guild, title};
    if (action == "setTitle")
      return Client::SetGuildMemberTitle{
          guild,
          player,
          title == Domain::InvalidId ? std::nullopt : std::optional<Domain::GuildTitleId>{title}
      };
    if (action == "setInfo") return Client::SetGuildInfo{guild, std::move(command.info)};
    if (action == "setListed") return Client::SetGuildListed{guild, command.listed};
    if (action == "apply") return Client::ApplyToGuild{guild};
    if (action == "withdraw") return Client::WithdrawGuildApplication{guild};
    if (action == "answerRequest") return Client::AnswerGuildJoinRequest{guild, player, command.accept};
    return Client::CreateGuild{std::move(command.name)};
  }

  // An unknown source is shown with the least trust.
  std::string_view OriginName(Domain::AnnouncementSource source)
  {
    return NameOf(OriginNames, source, Domain::AnnouncementSource::Server, OriginNames.back());
  }

  std::string_view KindName(Domain::AnnouncementKind kind)
  {
    return NameOf(KindNames, kind, Domain::AnnouncementKind::Announcement, KindNames.front());
  }

  // A mod label is already one checked line (by the server, or by the plugin
  // API for a local refusal); the UI and the log keep a bounded copy.
  constexpr std::size_t MaxLabelCodePoints = 64;

  std::string ModLabel(std::string_view value)
  {
    return std::string{Dreamsleeve::Utils::Text::Prefix(value, MaxLabelCodePoints)};
  }

  UiPlayer ToUiPlayer(const Domain::Player& source, Names& names, const UiSettings& settings)
  {
    auto player =
      ToUiAuthor(source.data, source.characterName, source.characterName.has_value() || source.characterNameWithheld, names, settings);

    const auto& details = source.details;
    player.level        = details.level;
    if (details.race) player.race = Text(details.race->name);
    if (details.place)
    {
      player.zone         = Text(details.place->worldspaceName);
      player.location     = Text(details.place->locationName);
      player.nearbyMarker = Text(details.place->nearbyMarkerName);
      player.markerKind   = Label(MarkerLabel(details.place->markerKind));
      player.interior     = details.place->isInterior;
    }
    else if (source.location)
      player.location = Text(source.location->location.locationName);

    player.activity       = Label(ActivityLabel(details.activity.kind));
    player.activityTarget = details.activity.targetName;
    player.lockDifficulty = Label(LockLabel(details.activity.lockDifficulty));
    if (details.activity.menuKey) player.menu = Label(MenuLabel(*details.activity.menuKey));
    player.gameStartedAt = details.gameStartedAtUnixMs;

    if (!source.actorValues.empty())
    {
      std::vector<UiActorValue> values;
      values.reserve(source.actorValues.size());
      for (const auto& [key, info] : source.actorValues)
      {
        UiActorValue value{key, info.displayName};
        if (const auto* resource = std::get_if<Domain::ResourceActorValue>(&info.state))
          value.value = UiResource{static_cast<double>(resource->current), static_cast<double>(resource->maximum)};
        else
          value.value = static_cast<double>(std::get<Domain::ScalarActorValue>(info.state).value);
        values.push_back(std::move(value));
      }

      std::ranges::sort(values, {}, &UiActorValue::key);
      player.actorValues = std::move(values);
    }

    return player;
  }

  constexpr std::string_view HiddenOwnText = "[скрыто фильтром]";

  // Each code point inside a flagged range becomes one star; whitespace stays so
  // a masked phrase keeps its shape. Ranges were validated on code point bounds.
  std::string MaskFlagged(std::string_view text, const std::vector<Domain::TextSpan>& spans)
  {
    std::string result;
    result.reserve(text.size());
    std::size_t at = 0;
    for (const auto& span : spans)
    {
      result.append(text.substr(at, span.start - at));
      for (std::size_t index = span.start; index < span.start + span.length; ++index)
      {
        const auto byte = text[index];
        if (Dreamsleeve::Utils::Text::Continuation(byte)) continue;
        result += byte == ' ' || byte == '\t' || byte == '\n' || byte == '\r' ? byte : '*';
      }
      at = span.start + span.length;
    }
    result.append(text.substr(at));
    return result;
  }

  // What the local filter lets through: the text, a masked copy, or nothing.
  // One's own hidden text stays as a placeholder so its pending row settles
  // visibly. The one rule for chat lines, bubbles and ground marks.
  std::optional<std::string> FilterText(
    const std::string&                   text,
    const std::vector<Domain::TextSpan>& flagged,
    const UiSettings&                    settings,
    bool                                 own)
  {
    if (flagged.empty() || settings.textFilter == "off") return text;
    if (settings.textFilter == "mask") return MaskFlagged(text, flagged);
    if (own) return std::string{HiddenOwnText};
    return std::nullopt;
  }

  std::optional<std::string> ShownText(const Domain::ChatMessage& message, const UiSettings& settings, bool own)
  {
    return FilterText(message.messageText, message.flagged, settings, own);
  }

  std::string_view MarkKindName(Domain::GroundMarkKind kind)
  {
    return NameOf(MarkKindNames, kind, Domain::GroundMarkKind::Note, MarkKindNames.front());
  }

  // A mark for the UI lists; a hidden own text shows the placeholder, a
  // hidden foreign text is left empty. Streamer mode drops the character snapshot.
  UiGroundMark ToUiGroundMark(const Domain::GroundMark& mark, const UiSettings& settings, bool own)
  {
    UiGroundMark result;
    result.id       = Id(mark.markId);
    result.kind     = std::string{MarkKindName(mark.kind)};
    result.text     = FilterText(mark.text, mark.flagged, settings, own).value_or(own ? std::string{HiddenOwnText} : std::string{});
    result.time     = Domain::ToUnixMilliseconds(mark.createdAt);
    result.location = std::format("{}:{:06X}", mark.placement.locationId.pluginName, mark.placement.locationId.localFormId);
    result.x        = mark.placement.position.X;
    result.y        = mark.placement.position.Y;
    result.z        = mark.placement.position.Z;
    if (!settings.streamerMode) result.character = mark.characterName;
    if (mark.gameDate) result.gameDate = FormatGameDate(*mark.gameDate, settings.markDateStyle);
    if (mark.characterLevel != 0) result.level = mark.characterLevel;
    if (mark.killerLevel != 0) result.killerLevel = mark.killerLevel;
    return result;
  }

  // The author is named from the snapshot taken at sending, never from the
  // character the player uses now.
  // Announcements are system lines; a client announcement names its player.
  UiMessage ToUiMessage(const Domain::ChatMessage& message, Names& names, const UiSettings& settings)
  {
    UiMessage result;
    result.id        = Id(message.messageId);
    result.channelId = Id(message.channelId);
    result.text      = message.messageText;
    result.time      = Domain::ToUnixMilliseconds(message.sentAt);
    if (message.announcement)
    {
      const auto& value   = *message.announcement;
      result.source       = "system";
      result.announcement = UiAnnouncement{std::string{OriginName(value.source)}, std::string{KindName(value.kind)}, std::nullopt};
      if (!value.signature.empty()) result.announcement->signature = ModLabel(value.signature);
    }
    if (message.author)
      result.author = ToUiAuthor(*message.author, message.characterName, message.characterName.has_value(), names, settings);
    return result;
  }

  // Chat projection with the local filter of server-flagged ranges applied.
  std::optional<UiMessage> ToShownMessage(
    const Domain::ChatMessage&      message,
    Names&                          names,
    const UiSettings&               settings,
    std::optional<Domain::PlayerId> self)
  {
    const bool own  = self && message.author && message.author->playerId == *self;
    auto       text = ShownText(message, settings, own);
    if (!text) return std::nullopt;
    auto result     = ToUiMessage(message, names, settings);
    result.filtered = *text != message.messageText;
    result.text     = std::move(*text);
    return result;
  }

  // The UI phase: a stopped application is disconnected, and a disconnected
  // one that signs in is authenticating.
  ConnectionPhase PhaseOf(const ClientStatus& status)
  {
    if (status.stopped) return ConnectionPhase::Disconnected;
    switch (status.phase)
    {
      case SessionPhase::Disconnected:
        return status.authenticating ? ConnectionPhase::Authenticating : ConnectionPhase::Disconnected;
      case SessionPhase::Connecting:
        return ConnectionPhase::Connecting;
      case SessionPhase::Opening:
        return ConnectionPhase::Opening;
      case SessionPhase::Ready:
        return ConnectionPhase::Connected;
      case SessionPhase::Disconnecting:
        return ConnectionPhase::Disconnecting;
      case SessionPhase::Faulted:
        return ConnectionPhase::Faulted;
    }
    return ConnectionPhase::Disconnected;
  }

  std::string_view PhaseName(const ClientStatus& status)
  {
    return NameOf(PhaseNames, PhaseOf(status), ConnectionPhase::Disconnected, PhaseNames.front());
  }

  ConnectionEvent ConnectionState(const ClientStatus& status)
  {
    return {.connected = PhaseOf(status) == ConnectionPhase::Connected, .phase = std::string{PhaseName(status)}};
  }

  // The saved account name a surface may show: streamer mode hides it.
  std::string ShownUsername(const ClientStatus& status, bool streamerMode)
  {
    return streamerMode ? std::string{} : status.savedUsername;
  }

  AuthEvent AuthState(const ClientStatus& status, bool streamerMode = false)
  {
    AuthEvent event;
    event.authenticating = status.authenticating;
    event.operation      = NameOf(OperationNames, status.authOperation, AuthOperation::None, OperationNames.front());
    event.failure        = NameOf(FailureNames, status.authFailure, ClientAuth::FailureCode::None, FailureNames.front());
    event.error          = ClipError(status.error);
    event.savedLogin     = status.savedLogin;
    event.savedUsername  = ShownUsername(status, streamerMode);
    event.registration =
      NameOf(RegistrationNames, status.methods.registration, ClientAuth::RegistrationMode::Unknown, RegistrationNames.front());
    event.steam         = status.methods.steam;
    event.browserFailed = !status.steamBrowserError.empty();
    event.phase         = PhaseName(status);
    return event;
  }

  MuteEvent MuteState(const ClientStatus& status)
  {
    if (!status.mute) return {};
    return {.muted = true, .reason = status.mute->reason, .until = status.mute->untilUnixMs};
  }

  RoleEvent Role(const ClientStatus& status)
  {
    return {.moderator = status.role == Domain::PlayerRole::Moderator};
  }

  std::string_view SanctionKindName(Domain::SanctionKind kind)
  {
    return NameOf(SanctionKindNames, kind, Domain::SanctionKind::Mute, SanctionKindNames.front());
  }

  // Bridge::Admit let only SanctionKindNames through.
  Domain::SanctionKind SanctionKindOf(std::string_view name)
  {
    return name == SanctionKindNames[1] ? Domain::SanctionKind::Ban : Domain::SanctionKind::Mute;
  }

  UiSanction ToUiSanction(const Domain::Sanction& sanction, std::optional<std::string> name)
  {
    return {
        .playerId = Id(sanction.playerId),
        .name     = std::move(name),
        .kind     = std::string{SanctionKindName(sanction.kind)},
        .reason   = sanction.reason,
        .issuedAt = sanction.issuedAtUnixMs,
        .until    = sanction.untilUnixMs
    };
  }

  SessionEndedEvent Ended(const Domain::SessionEnd& end)
  {
    return {
        .reason = std::string{NameOf(EndNames, end.reason, Domain::SessionEndReason::AccessRevoked, EndNames.back())},
        .text   = end.text,
        .until  = end.untilUnixMs
    };
  }

}
