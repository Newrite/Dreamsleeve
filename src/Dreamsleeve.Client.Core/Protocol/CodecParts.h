#pragma once

// Implementation-only declarations: included AFTER the module declaration in
// .cpp units. Protobuf definitions stay in their global module fragments, never IFCs.
namespace Dreamsleeve::Client::Wire::Detail
{
  namespace P = Dreamsleeve::Protocol::Chat;

  inline auto Failure(ErrorCode code, std::string field)
  {
    return std::unexpected{
        Error{code, std::move(field)}
    };
  }

  inline auto Invalid(std::string field)
  {
    return Failure(ErrorCode::InvalidPayload, std::move(field));
  }

  // Milliseconds of the years 1..9999, the range both sides can format.
  inline constexpr std::int64_t MinUnixMs = -62135596800000LL;
  inline constexpr std::int64_t MaxUnixMs = 253402300799999LL;

  constexpr bool ValidUnixMs(std::int64_t value)
  {
    return value >= MinUnixMs && value <= MaxUnixMs;
  }

  using Domain::Checks::Finite;
  using Domain::Checks::ValidGameDate;
  using Domain::Checks::ValidKey;
  using Domain::Checks::ValidPlacement;

  inline Domain::FormKey KeyOf(const P::FormKey& source)
  {
    return {source.plugin_name(), source.local_form_id()};
  }

  inline void WriteKey(P::FormKey& target, const Domain::FormKey& value)
  {
    target.set_plugin_name(value.pluginName);
    target.set_local_form_id(value.localFormId);
  }

  inline Domain::Position PositionOf(const P::Position& source)
  {
    return {source.x(), source.y(), source.z()};
  }

  inline void WritePosition(P::Position& target, const Domain::Position& value)
  {
    target.set_x(value.X);
    target.set_y(value.Y);
    target.set_z(value.Z);
  }

  inline Domain::Rotation RotationOf(const P::Rotation& source)
  {
    return {source.x(), source.y(), source.z()};
  }

  inline void WriteRotation(P::Rotation& target, const Domain::Rotation& value)
  {
    target.set_x(value.X);
    target.set_y(value.Y);
    target.set_z(value.Z);
  }

  void                           WritePose(P::MovementPose&, const Domain::MovementPose&);
  Result<PlayerLocationUpdated>  ReadVisibility(const P::PlayerVisibilityChanged&);
  void                           WritePlayerUpdate(P::UpdatePlayer&, const PlayerUpdate&);
  Result<Domain::PlayerData>     Profile(const P::PlayerProfile&);
  Result<Domain::Player>         Player(const Configuration&, const P::PlayerInfo&);
  Result<PlayerMetadataUpdated>  ReadMetadata(const Configuration&, const P::PlayerMetadataChanged&);
  Result<PlayerMovementReceived> ReadMovement(const P::PlayerMoved&);

  void                        WriteChat(P::SendChat&, const SendChat&);
  void                        WriteAnnouncement(P::PostAnnouncement&, const PostAnnouncement&);
  Result<Domain::ChatMessage> Message(const P::ChatMessage&);
  Domain::AnnouncementPolicy  Policy(const P::AnnouncementPolicy&);
  // Ranges inside the text, ascending, disjoint, on code point boundaries.
  Result<std::vector<Domain::TextSpan>> ReadFlagged(const std::string&, const google::protobuf::RepeatedPtrField<P::TextSpan>&);

  void                           WriteNote(P::PlaceGroundNote&, const PlaceGroundNote&);
  void                           WriteDeath(P::ReportDeath&, const ReportDeath&);
  Result<Domain::GroundMark>     Mark(const P::GroundMark&);
  Result<GroundMarksChanged>     ReadMarksChanged(const P::GroundMarksChanged&);
  Result<OwnGroundMarksReplaced> ReadOwnMarks(const P::OwnGroundMarks&);

  void                  WriteSession(P::OpenSession&, const OpenSession&);
  Result<SessionOpened> Welcome(const Configuration&, std::uint64_t, const P::SessionOpened&);
  Domain::MuteState     Mute(const P::MuteState&);
  Result<SessionEnded>  Ended(const P::SessionEnded&);

}
