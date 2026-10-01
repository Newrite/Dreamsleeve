namespace Dreamsleeve.Server.Domain

open System
open FSharp.UMX

/// Discipline of the server: a mute takes a player's voice, a ban the game
/// itself, for a term or until lifted. Administrators and moderators issue
/// them; storage, sessions and HTTP apply what these rules decide.
[<AutoOpen>]
module SanctionUMX =
    [<Measure>]
    type sanctionId
    [<Measure>]
    type sanctionReason

type SanctionId = int64<sanctionId>
type SanctionReason = string<sanctionReason>

[<RequireQualifiedAccess>]
type SanctionKind =
    /// No writing: chat, notes, announcements and the display name. Reading stays.
    | Mute
    /// No session: sign-in and resume are refused and a live session ends.
    | Ban

/// Where a sanction holds. The server is the only scope now; a guild's own
/// discipline will be another case carrying its guild.
[<RequireQualifiedAccess>]
type SanctionScope = | Server

/// How long a sanction holds.
[<RequireQualifiedAccess>]
type SanctionTerm =
    | For of TimeSpan
    | UntilLifted

/// Who issued a sanction: a panel administrator or a moderator in the game.
[<RequireQualifiedAccess>]
type SanctionIssuer =
    | Admin of AdminId
    | Moderator of PlayerId

/// A sanction in force. A lifted one is history, kept only by storage and audit.
type Sanction = {
    Id: SanctionId
    Target: PlayerId
    Kind: SanctionKind
    Scope: SanctionScope
    Reason: SanctionReason
    /// Absent once the issuer's account is gone.
    IssuedBy: SanctionIssuer voption
    IssuedAt: DateTimeOffset
    /// Absent: until lifted.
    Expires: DateTimeOffset voption
}

/// A request to discipline a player, already validated.
type SanctionOrder = {
    Target: PlayerId
    Kind: SanctionKind
    Term: SanctionTerm
    Reason: SanctionReason
    IssuedBy: SanctionIssuer
    /// A ban also covers the devices the player signed in from, for as long as
    /// it holds; ignored for a mute.
    Devices: bool
}

[<RequireQualifiedAccess>]
type SanctionError =
    | PlayerNotFound
    /// The issuer does not rank above the target.
    | NotAllowed
    /// Nothing of that kind is in force.
    | NotActive

[<RequireQualifiedAccess>]
module SanctionId =
    let value (id: SanctionId) : int64 = UMX.untag id

    /// Storage issues positive IDs.
    let create raw : Result<SanctionId, DomainError> =
        if raw <= 0L then Error(DomainError.InvalidId "SanctionId") else Ok(UMX.tag<sanctionId> raw)

[<RequireQualifiedAccess>]
module SanctionKind =
    let all = [ SanctionKind.Mute; SanctionKind.Ban ]

    /// Stored form.
    let toInt kind =
        match kind with
        | SanctionKind.Mute -> 0
        | SanctionKind.Ban -> 1

    let ofInt value =
        match value with
        | 0 -> ValueSome SanctionKind.Mute
        | 1 -> ValueSome SanctionKind.Ban
        | _ -> ValueNone

    /// Stable form and API key.
    let key kind =
        match kind with
        | SanctionKind.Mute -> "mute"
        | SanctionKind.Ban -> "ban"

    let ofKey (text: string) = all |> List.tryFind (fun kind -> key kind = text)

[<RequireQualifiedAccess>]
module SanctionReason =
    [<Literal>]
    let MaxLength = 200

    let value (reason: SanctionReason) : string = UMX.untag reason

    /// Required, one line, trimmed: the player reads it, the audit keeps it.
    let create raw : Result<SanctionReason, DomainError> =
        PrimitiveValidation.name "SanctionReason" MaxLength raw |> Result.map UMX.tag

[<RequireQualifiedAccess>]
module SanctionTerm =
    /// The longest term in minutes, ten years; longer is until lifted.
    [<Literal>]
    let MaxMinutes = 5_256_000

    /// None: until lifted.
    let create (minutes: int voption) : Result<SanctionTerm, DomainError> =
        match minutes with
        | ValueNone -> Ok SanctionTerm.UntilLifted
        | ValueSome minutes when minutes >= 1 && minutes <= MaxMinutes -> Ok(SanctionTerm.For(TimeSpan.FromMinutes(float minutes)))
        | ValueSome minutes -> Error(DomainError.InvalidLimit("SanctionTerm", minutes))

[<RequireQualifiedAccess>]
module Sanction =
    /// When a sanction of this term issued at now ends; absent: until lifted.
    let expiry (now: DateTimeOffset) term =
        match term with
        | SanctionTerm.For term -> ValueSome(now + term)
        | SanctionTerm.UntilLifted -> ValueNone

    /// The sanction an order becomes when issued at now.
    let issue id (now: DateTimeOffset) (order: SanctionOrder) : Sanction = {
        Id = id
        Target = order.Target
        Kind = order.Kind
        Scope = SanctionScope.Server
        Reason = order.Reason
        IssuedBy = ValueSome order.IssuedBy
        IssuedAt = now
        Expires = expiry now order.Term
    }

    /// In force at now: its term has not ended.
    let activeAt (now: DateTimeOffset) (sanction: Sanction) =
        match sanction.Expires with
        | ValueNone -> true
        | ValueSome expires -> now < expires

    /// The one sanction of a kind in force at now, if any.
    let find kind now (sanctions: Sanction seq) =
        sanctions |> Seq.tryFind (fun sanction -> sanction.Kind = kind && activeAt now sanction) |> ValueOption.ofOption
