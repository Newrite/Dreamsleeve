module;
#include "protocol.pb.h"

module Dreamsleeve.Client.ProtocolCodec;
#include "CodecParts.h"

// Unknown wire values are handled; every newly generated named case must be listed.
#pragma warning(error : 4061 4062)

namespace Dreamsleeve::Client::Wire::Detail
{

  Result<Domain::ChatMessage> Message(const P::ChatMessage& message)
  {
    if (message.message_id() == 0 || message.channel_id() == 0) return Invalid("message");
    if (message.sent_at_unix_ms() < -62135596800000LL || message.sent_at_unix_ms() > 253402300799999LL) return Invalid("sent_at_unix_ms");

    auto author = Profile(message.author());
    if (!author) return std::unexpected{author.error()};

    return Domain::ChatMessage{
        message.message_id(),
        message.channel_id(),
        std::move(*author),
        message.text(),
        Domain::FromUnixMilliseconds(message.sent_at_unix_ms()),
        message.has_character_name() ? std::optional{message.character_name()} : std::nullopt
    };
  }

  void WriteChat(P::SendChat& target, const SendChat& value)
  {
    target.set_channel_id(value.channelId);
    target.set_text(value.text);
  }

}
