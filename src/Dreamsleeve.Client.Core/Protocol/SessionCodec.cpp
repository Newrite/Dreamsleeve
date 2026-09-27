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
    if (source.self_player_id() == 0 || source.global_channel_id() == 0) return Invalid("session_opened");
    if (
      static_cast<std::size_t>(source.players_size()) > config.maxInitialPlayers ||
      static_cast<std::size_t>(source.recent_messages_size()) > config.maxRecentMessages)
      return Invalid("initial_count");

    SessionOpened result{requestId, source.self_player_id(), source.global_channel_id()};
    for (const auto& player : source.players())
    {
      auto decoded = Player(config, player);
      if (!decoded) return std::unexpected{decoded.error()};

      result.players.push_back(std::move(*decoded));
    }

    for (const auto& value : source.recent_messages())
    {
      auto message = Message(value);
      if (!message) return std::unexpected{message.error()};

      result.recentMessages.push_back(std::move(*message));
    }

    return result;
  }

  void WriteSession(P::OpenSession& target, const OpenSession& value)
  {
    target.set_session_ticket(value.sessionTicket);
  }

  bool ValidTicket(std::string_view ticket)
  {
    return ticket.size() == 43 && std::ranges::all_of(ticket, [](unsigned char c) {
             return (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
           });
  }

}
