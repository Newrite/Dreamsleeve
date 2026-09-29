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

    // Ranges must lie inside the text, ascend without overlap and cut on code
    // point boundaries, so a client can mask them without breaking UTF-8.
    std::vector<Domain::TextSpan> flagged;
    flagged.reserve(static_cast<std::size_t>(message.flagged_size()));
    const auto&   text = message.text();
    std::uint64_t floor{};
    const auto    boundary = [&](std::uint64_t at) {
      return at == text.size() || (static_cast<unsigned char>(text[at]) & 0xC0) != 0x80;
    };
    for (const auto& span : message.flagged())
    {
      const auto end = static_cast<std::uint64_t>(span.start()) + span.length();
      if (span.length() == 0 || span.start() < floor || end > text.size() || !boundary(span.start()) || !boundary(end))
        return Invalid("flagged");
      flagged.push_back({span.start(), span.length()});
      floor = end;
    }

    return Domain::ChatMessage{
        message.message_id(),
        message.channel_id(),
        std::move(*author),
        message.text(),
        Domain::FromUnixMilliseconds(message.sent_at_unix_ms()),
        message.has_character_name() ? std::optional{message.character_name()} : std::nullopt,
        std::move(flagged)
    };
  }

  void WriteChat(P::SendChat& target, const SendChat& value)
  {
    target.set_channel_id(value.channelId);
    target.set_text(value.text);
  }

}
