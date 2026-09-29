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

    // Only a server announcement has no author; the system is not a player.
    const bool serverAnnouncement = message.has_announcement() && message.announcement().source() == P::ANNOUNCEMENT_SOURCE_SERVER;
    std::optional<Domain::PlayerData> author;
    if (message.has_author())
    {
      auto profile = Profile(message.author());
      if (!profile) return std::unexpected{profile.error()};
      author = std::move(*profile);
    }
    else if (!serverAnnouncement)
      return Invalid("author");

    auto flagged = ReadFlagged(message.text(), message.flagged());
    if (!flagged) return std::unexpected{flagged.error()};

    // Values are kept as sent, unknown numbers included; the host treats what it
    // does not know as untrusted.
    std::optional<Domain::Announcement> announcement;
    if (message.has_announcement())
      announcement = Domain::Announcement{
          static_cast<Domain::AnnouncementSource>(message.announcement().source()),
          static_cast<Domain::AnnouncementKind>(message.announcement().kind()),
          message.announcement().signature()
      };

    return Domain::ChatMessage{
        message.message_id(),
        message.channel_id(),
        std::move(author),
        message.text(),
        Domain::FromUnixMilliseconds(message.sent_at_unix_ms()),
        message.has_character_name() ? std::optional{message.character_name()} : std::nullopt,
        std::move(*flagged),
        std::move(announcement)
    };
  }

  // Ranges must lie inside the text, ascend without overlap and cut on code
  // point boundaries, so a client can mask them without breaking UTF-8.
  Result<std::vector<Domain::TextSpan>> ReadFlagged(const std::string& text, const google::protobuf::RepeatedPtrField<P::TextSpan>& spans)
  {
    std::vector<Domain::TextSpan> flagged;
    flagged.reserve(static_cast<std::size_t>(spans.size()));
    std::uint64_t floor{};
    const auto    boundary = [&](std::uint64_t at) {
      return at == text.size() || (static_cast<unsigned char>(text[at]) & 0xC0) != 0x80;
    };
    for (const auto& span : spans)
    {
      const auto end = static_cast<std::uint64_t>(span.start()) + span.length();
      if (span.length() == 0 || span.start() < floor || end > text.size() || !boundary(span.start()) || !boundary(end))
        return Invalid("flagged");
      flagged.push_back({span.start(), span.length()});
      floor = end;
    }
    return flagged;
  }

  void WriteChat(P::SendChat& target, const SendChat& value)
  {
    target.set_channel_id(value.channelId);
    target.set_text(value.text);
  }

  void WriteAnnouncement(P::PostAnnouncement& target, const PostAnnouncement& value)
  {
    target.set_channel_id(value.channelId);
    target.set_text(value.text);
    target.set_kind(static_cast<P::AnnouncementKind>(value.kind));
    target.set_source(static_cast<P::ClientAnnouncementSource>(value.source));
    target.set_signature(value.signature);
  }

  Domain::AnnouncementPolicy Policy(const P::AnnouncementPolicy& source)
  {
    Domain::AnnouncementPolicy result{{}, source.max_text_length(), source.max_signature_length()};
    for (const auto value : source.allowed_sources())
      result.allowedSources.push_back(static_cast<Domain::ClientAnnouncementSource>(value));
    return result;
  }

}
