module;
#include "protocol.pb.h"

module Dreamsleeve.Client.ProtocolCodec;
#include "CodecParts.h"

// Unknown wire values are handled; every newly generated named case must be listed.
#pragma warning(error : 4061 4062)

namespace Dreamsleeve::Client::Wire::Detail
{

  Result<SessionOpened> Welcome(const Configuration& config, std::uint64_t requestId, const P::SessionOpened& source)
  {
    if (source.self_player_id() == Domain::InvalidId || source.channels().empty() || !source.has_announcements())
      return Invalid("session_opened");
    if (static_cast<std::size_t>(source.players_size()) > config.maxInitialPlayers) return Invalid("initial_count");

    SessionOpened result{requestId, source.self_player_id()};
    result.serverName    = source.server_name();
    result.announcements = Policy(source.announcements());
    if (
      !P::HiddenIdentity_IsValid(source.hidden_identity()) ||
      source.has_own_pseudonym() != (source.hidden_identity() != P::HIDDEN_IDENTITY_NONE))
      return Invalid("hidden_identity");
    if (source.has_own_pseudonym()) result.ownPseudonym = source.own_pseudonym();
    result.hiding = static_cast<Domain::HiddenIdentity>(source.hidden_identity());
    for (const auto& player : source.players())
    {
      auto decoded = Player(config, player);
      if (!decoded) return std::unexpected{decoded.error()};

      result.players.push_back(std::move(*decoded));
    }

    // Server-wide kinds exist once each, which also bounds the welcome size.
    // Channel identity and message rules are checked by the model on registration and merge.
    bool global = false, system = false;
    for (const auto& channel : source.channels())
    {
      if (channel.channel_id() == Domain::InvalidId) return Invalid("channel_id");
      if (channel.kind() != P::CHAT_CHANNEL_KIND_GLOBAL && channel.kind() != P::CHAT_CHANNEL_KIND_SYSTEM) return Invalid("kind");
      auto& seen = channel.kind() == P::CHAT_CHANNEL_KIND_GLOBAL ? global : system;
      if (seen) return Invalid("kind");
      seen = true;
      if (static_cast<std::size_t>(channel.recent_messages_size()) > config.maxRecentMessages) return Invalid("initial_count");

      ChannelOpened opened{channel.channel_id(), static_cast<Domain::ChatChannelKind>(channel.kind())};
      for (const auto& value : channel.recent_messages())
      {
        auto message = Message(value);
        if (!message) return std::unexpected{message.error()};

        opened.recentMessages.push_back(std::move(*message));
      }
      result.channels.push_back(std::move(opened));
    }

    return result;
  }

  void WriteSession(P::OpenSession& target, const OpenSession& value)
  {
    target.set_session_ticket(value.sessionTicket);
    target.set_hidden_identity(static_cast<P::HiddenIdentity>(value.hiding));
  }

}
