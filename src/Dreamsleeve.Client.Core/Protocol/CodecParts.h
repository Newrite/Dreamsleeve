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

  bool                           ValidPlacement(const Domain::GroundMarkPlacement&);
  void                           WriteNote(P::PlaceGroundNote&, const PlaceGroundNote&);
  void                           WriteDeath(P::ReportDeath&, const ReportDeath&);
  Result<Domain::GroundMark>     Mark(const P::GroundMark&);
  Result<GroundMarksChanged>     ReadMarksChanged(const P::GroundMarksChanged&);
  Result<OwnGroundMarksReplaced> ReadOwnMarks(const P::OwnGroundMarks&);

  void                  WriteSession(P::OpenSession&, const OpenSession&);
  bool                  ValidTicket(std::string_view);
  Result<SessionOpened> Welcome(const Configuration&, std::uint64_t, const P::SessionOpened&);

}
