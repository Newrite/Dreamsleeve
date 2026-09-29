namespace Dreamsleeve.Server.Domain

open System
open System.Collections.Generic
open FSharp.UMX

/// The two kinds of marks. Quotas and lifetimes are per kind.
[<RequireQualifiedAccess>]
type GroundMarkKind =
    /// Written by the player on purpose.
    | Note
    /// Left by the client where the character died.
    | Death

/// The text of a mark, typed by its kind so a note and a death label cannot be confused.
[<RequireQualifiedAccess>]
type GroundMarkBody =
    | Note of GroundNoteText
    | Death of DeathMarkText

[<RequireQualifiedAccess>]
module GroundMarkBody =
    let kind = function
        | GroundMarkBody.Note _ -> GroundMarkKind.Note
        | GroundMarkBody.Death _ -> GroundMarkKind.Death

    let text = function
        | GroundMarkBody.Note value -> GroundNoteText.value value
        | GroundMarkBody.Death value -> DeathMarkText.value value

/// Where a mark stands: the space, the point and the author's heading (Z angle,
/// radians) so a client can turn the visual the way the author looked.
[<Struct>]
type GroundMarkPlacement = private {
    locationId: LocationId
    position: Position
    heading: Radian
} with
    member this.LocationId = this.locationId
    member this.Position = this.position
    member this.Heading = this.heading

[<RequireQualifiedAccess>]
module GroundMarkPlacement =
    /// Components have already passed their own factories (finite coordinates and angle).
    let create locationId position heading : GroundMarkPlacement =
        { locationId = locationId; position = position; heading = heading }

    let private withinSquared (radius: WorldUnit) (origin: Position) (target: Position) =
        let radius64 = LanguagePrimitives.FloatWithMeasure<worldUnit> (float radius)
        Position.distanceSquared origin target <= radius64 * radius64

    /// Soft placement check (DomainSpec §10): an unknown author position passes,
    /// another space fails, and a zero limit disables the distance rule.
    let isNear (maxDistance: WorldUnit) (lastKnown: PlayerLocation voption) (placement: GroundMarkPlacement) =
        match lastKnown with
        | ValueNone -> true
        | ValueSome known when known.Location.LocationId <> placement.LocationId -> false
        | ValueSome known -> maxDistance = 0.0f<worldUnit> || withinSquared maxDistance known.Position placement.Position

    /// Same space and within the radius, boundary included. Without an
    /// observer position nothing on the ground is visible.
    let isVisibleFrom (radius: WorldUnit) (observer: PlayerLocation voption) (placement: GroundMarkPlacement) =
        match observer with
        | ValueNone -> false
        | ValueSome origin ->
            origin.Location.LocationId = placement.LocationId && withinSquared radius origin.Position placement.Position

/// Persistent server data, unlike chat and poses: survives restarts and the
/// author's reloads. Names are not stored; the author's profile is looked up by ID.
type GroundMark = private {
    id: GroundMarkId
    author: PlayerId
    characterName: CharacterName voption
    body: GroundMarkBody
    flagged: TextSpan list
    placement: GroundMarkPlacement
    createdAt: DateTimeOffset
} with
    member this.Id = this.id
    member this.Author = this.author
    /// Published character name at placement, like ChatMessage.CharacterName; never updated later.
    member this.CharacterName = this.characterName
    member this.Body = this.body
    member this.Kind = GroundMarkBody.kind this.body
    member this.Text = GroundMarkBody.text this.body
    /// Word-list ranges of Text in UTF-8 bytes, like ChatMessage.Flagged.
    member this.Flagged = this.flagged
    member this.Placement = this.placement
    member this.CreatedAt = this.createdAt

/// Per-kind quotas, lifetimes and distances. Validated once from configuration.
type GroundMarkRules = private {
    maxNotesPerPlayer: int
    maxDeathMarksPerPlayer: int
    noteTtl: TimeSpan voption
    deathMarkTtl: TimeSpan voption
    visibilityDistance: WorldUnit
    maxPlacementDistance: WorldUnit
} with
    member this.MaxNotesPerPlayer = this.maxNotesPerPlayer
    member this.MaxDeathMarksPerPlayer = this.maxDeathMarksPerPlayer
    /// Absent means the kind never expires.
    member this.NoteTtl = this.noteTtl
    member this.DeathMarkTtl = this.deathMarkTtl
    member this.VisibilityDistance = this.visibilityDistance
    /// Zero disables the distance part of the placement check.
    member this.MaxPlacementDistance = this.maxPlacementDistance

[<RequireQualifiedAccess>]
module GroundMarkRules =
    let private radius field (raw: float32) =
        if not (Single.IsFinite raw) || raw < 0.0f then Error (DomainError.NonFiniteNumber field)
        else Ok (LanguagePrimitives.Float32WithMeasure<worldUnit> raw)

    let private lifetime field days =
        if days < 0 then Error (DomainError.InvalidLimit (field, days))
        elif days = 0 then Ok ValueNone
        else Ok (ValueSome (TimeSpan.FromDays (float days)))

    /// Quotas are at least one per kind; a TTL of zero days means no expiry.
    let create maxNotesPerPlayer maxDeathMarksPerPlayer noteTtlDays deathMarkTtlDays visibilityDistance maxPlacementDistance =
        if maxNotesPerPlayer < 1 then Error (DomainError.InvalidLimit ("maxNotesPerPlayer", maxNotesPerPlayer))
        elif maxDeathMarksPerPlayer < 1 then Error (DomainError.InvalidLimit ("maxDeathMarksPerPlayer", maxDeathMarksPerPlayer))
        else
            lifetime "noteTtlDays" noteTtlDays |> Result.bind (fun note ->
            lifetime "deathMarkTtlDays" deathMarkTtlDays |> Result.bind (fun death ->
            radius "visibilityDistance" visibilityDistance |> Result.bind (fun visibility ->
            radius "maxPlacementDistance" maxPlacementDistance |> Result.map (fun placement ->
                { maxNotesPerPlayer = maxNotesPerPlayer
                  maxDeathMarksPerPlayer = maxDeathMarksPerPlayer
                  noteTtl = note
                  deathMarkTtl = death
                  visibilityDistance = visibility
                  maxPlacementDistance = placement }))))

    let quota (rules: GroundMarkRules) kind =
        match kind with
        | GroundMarkKind.Note -> rules.MaxNotesPerPlayer
        | GroundMarkKind.Death -> rules.MaxDeathMarksPerPlayer

    let ttl (rules: GroundMarkRules) kind =
        match kind with
        | GroundMarkKind.Note -> rules.NoteTtl
        | GroundMarkKind.Death -> rules.DeathMarkTtl

[<RequireQualifiedAccess>]
module GroundMark =
    /// Components have already passed their own domain validation. The server
    /// supplies the ID and the creation time; the author is the account.
    let create id author body placement (createdAt: DateTimeOffset) : GroundMark =
        { id = id; author = author; characterName = ValueNone; body = body; flagged = []; placement = placement; createdAt = createdAt.ToUniversalTime() }

    /// The author's published character name at placement; a withheld name stays absent.
    let withCharacterName name (mark: GroundMark) = { mark with characterName = name }

    /// Spans come from moderation of this exact text.
    let withFlagged spans (mark: GroundMark) = { mark with flagged = spans }

    let expiresAt rules (mark: GroundMark) =
        GroundMarkRules.ttl rules mark.Kind |> ValueOption.map (fun lifetime -> mark.CreatedAt + lifetime)

    /// Expiry is inclusive: a mark whose lifetime ends exactly now is gone.
    let isExpired rules (now: DateTimeOffset) (mark: GroundMark) =
        match expiresAt rules mark with
        | ValueSome deadline -> now >= deadline
        | ValueNone -> false

    let isVisibleFrom (radius: WorldUnit) observer (mark: GroundMark) =
        GroundMarkPlacement.isVisibleFrom radius observer mark.Placement

/// All marks of the server, owned by one agent. Per author and kind the IDs are
/// kept ordered so the oldest mark is evicted first. No live collection leaves.
[<NoEquality; NoComparison>]
type GroundMarkStorage = private {
    marks: Dictionary<GroundMarkId, GroundMark>
    byAuthor: Dictionary<struct (PlayerId * GroundMarkKind), SortedSet<GroundMarkId>>
}

[<RequireQualifiedAccess>]
module GroundMarkStorage =
    let create () : GroundMarkStorage = { marks = Dictionary(); byAuthor = Dictionary() }

    let count (storage: GroundMarkStorage) = storage.marks.Count

    let tryFind id (storage: GroundMarkStorage) =
        match storage.marks.TryGetValue id with
        | true, mark -> ValueSome mark
        | false, _ -> ValueNone

    let contains id (storage: GroundMarkStorage) = storage.marks.ContainsKey id

    let countOf author kind (storage: GroundMarkStorage) =
        match storage.byAuthor.TryGetValue(struct (author, kind)) with
        | true, ids -> ids.Count
        | false, _ -> 0

    let authorHasMarks author (storage: GroundMarkStorage) =
        countOf author GroundMarkKind.Note storage > 0 || countOf author GroundMarkKind.Death storage > 0

    /// The mark a new one of this author and kind would evict: the oldest,
    /// once the quota is reached. IDs are monotonic, so the smallest is the oldest.
    let evictionCandidate rules author kind (storage: GroundMarkStorage) =
        match storage.byAuthor.TryGetValue(struct (author, kind)) with
        | true, ids when ids.Count >= GroundMarkRules.quota rules kind -> tryFind ids.Min storage
        | true, _ | false, _ -> ValueNone

    /// Returns the removed mark so its observers can be told.
    let remove id (storage: GroundMarkStorage) =
        match storage.marks.TryGetValue id with
        | false, _ -> ValueNone
        | true, mark ->
            storage.marks.Remove id |> ignore
            let key = struct (mark.Author, mark.Kind)
            match storage.byAuthor.TryGetValue key with
            | true, ids ->
                ids.Remove id |> ignore
                if ids.Count = 0 then storage.byAuthor.Remove key |> ignore
            | false, _ -> ()
            ValueSome mark

    /// Stores a mark under the per-kind quota of its author (soft rule: one
    /// oldest mark of the same author and kind gives way, others are untouched).
    /// Returns the evicted mark, if any. A repeated ID is refused unchanged.
    let add rules (mark: GroundMark) (storage: GroundMarkStorage) =
        if storage.marks.ContainsKey mark.Id then
            Error (DomainError.DuplicateGroundMark mark.Id)
        else
            let evicted =
                match evictionCandidate rules mark.Author mark.Kind storage with
                | ValueSome oldest -> remove oldest.Id storage
                | ValueNone -> ValueNone
            storage.marks.Add(mark.Id, mark)
            let key = struct (mark.Author, mark.Kind)
            let ids =
                match storage.byAuthor.TryGetValue key with
                | true, existing -> existing
                | false, _ ->
                    let created = SortedSet<GroundMarkId>()
                    storage.byAuthor.Add(key, created)
                    created
            ids.Add mark.Id |> ignore
            Ok evicted

    /// Marks past their lifetime at now, oldest first. Removal is the caller's decision.
    let expired rules now (storage: GroundMarkStorage) =
        storage.marks.Values
        |> Seq.filter (GroundMark.isExpired rules now)
        |> Seq.sortBy _.Id
        |> List.ofSeq

    /// Detached, ascending by ID.
    let snapshot (storage: GroundMarkStorage) =
        storage.marks.Values |> Seq.sortBy _.Id |> List.ofSeq

    /// Every mark of one author, both kinds, ascending by ID.
    let ofAuthor author (storage: GroundMarkStorage) =
        [ GroundMarkKind.Note; GroundMarkKind.Death ]
        |> List.collect (fun kind ->
            match storage.byAuthor.TryGetValue(struct (author, kind)) with
            | true, ids -> ids |> Seq.choose (fun id -> tryFind id storage |> ValueOption.toOption) |> List.ofSeq
            | false, _ -> [])
        |> List.sortBy _.Id
