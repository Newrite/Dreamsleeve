namespace Dreamsleeve.Server.Domain

open System
open System.Collections.Generic
open System.Text
open FSharp.UMX

/// Guilds (docs/DomainSpecRu.MD §4.7, §10): long-lived groups with a master,
/// officers and members, their own chat and discipline. Limits are soft: a
/// lowered limit removes nobody and refuses only what would grow the excess.
[<AutoOpen>]
module GuildUMX =
    [<Measure>]
    type guildName

type GuildName = string<guildName>

[<RequireQualifiedAccess>]
module GuildName =
    let value (name: GuildName) : string = UMX.untag name

    let private lettersAndDigits (source: string) =
        if source.EnumerateRunes() |> Seq.forall Rune.IsLetterOrDigit then ValueNone
        else ValueSome TextError.InvalidCharacters

    /// Letters and digits of any alphabet only, in NFC: no spaces, punctuation
    /// or controls; minLength..maxLength scalar values. The word list and
    /// uniqueness are the owner's checks; a name never changes.
    let create minLength maxLength raw : Result<GuildName, DomainError> =
        if minLength <= 0 || minLength > maxLength then Error(DomainError.InvalidLimit("GuildName", minLength))
        else
            PrimitiveValidation.text "GuildName" maxLength PrimitiveValidation.nfcTrim false lettersAndDigits raw
            |> Result.bind (fun canonical ->
                if PrimitiveValidation.scalarCount canonical < minLength then
                    Error(DomainError.InvalidText("GuildName", TextError.TooShort minLength))
                else Ok(UMX.tag<guildName> canonical))

    /// Names are unique regardless of case: "Стражи" and "стражи" are one name.
    let key (name: GuildName) = (value name).ToLowerInvariant()

[<RequireQualifiedAccess>]
type GuildRole =
    | Member
    | Officer
    | Master

[<RequireQualifiedAccess>]
module GuildRole =
    let all = [ GuildRole.Member; GuildRole.Officer; GuildRole.Master ]

    /// Stored form; also the rank.
    let toInt role =
        match role with
        | GuildRole.Member -> 0
        | GuildRole.Officer -> 1
        | GuildRole.Master -> 2

    let ofInt value = all |> List.tryFind (fun role -> toInt role = value) |> Option.toValueOption

    /// Stable form and API key.
    let key role =
        match role with
        | GuildRole.Member -> "member"
        | GuildRole.Officer -> "officer"
        | GuildRole.Master -> "master"

    let ofKey (text: string) = all |> List.tryFind (fun role -> key role = text)

    /// Discipline follows "role above target": the master acts on everyone
    /// else, an officer on members, a member on nobody.
    let outranks actor target = toInt actor > toInt target

    /// The master and officers invite.
    let mayInvite role = role <> GuildRole.Member

/// A guild's own mute: the member reads the guild chat but does not write in it.
type GuildMute = {
    Reason: SanctionReason
    IssuedBy: PlayerId
    IssuedAt: DateTimeOffset
    /// Absent: until lifted.
    Expires: DateTimeOffset voption
}

[<RequireQualifiedAccess>]
module GuildMute =
    let activeAt (now: DateTimeOffset) (mute: GuildMute) =
        match mute.Expires with
        | ValueNone -> true
        | ValueSome expires -> now < expires

type GuildMember = {
    Player: PlayerId
    Role: GuildRole
    JoinedAt: DateTimeOffset
    Mute: GuildMute voption
}

[<RequireQualifiedAccess>]
module GuildMember =
    /// The mute in force at now, if any.
    let mute now (membership: GuildMember) = membership.Mute |> ValueOption.filter (GuildMute.activeAt now)

/// A pending invitation; accepting it checks the limits at that moment.
type GuildInvite = {
    Guild: GuildId
    Player: PlayerId
    InvitedBy: PlayerId
    CreatedAt: DateTimeOffset
    Expires: DateTimeOffset
}

/// The server's guild limits (docs/DomainSpecRu.MD §10), checked when an
/// action would grow what they bound.
type GuildLimits =
    private {
        maxGuilds: int
        maxGuildsPerPlayer: int
        maxMembers: int
        maxInvites: int
        nameMinLength: int
        nameMaxLength: int
        inviteLifetime: TimeSpan
    }

    member this.MaxGuilds = this.maxGuilds
    /// Guilds a player is in, the ones they master included.
    member this.MaxGuildsPerPlayer = this.maxGuildsPerPlayer
    member this.MaxMembers = this.maxMembers
    /// Pending invitations of one guild.
    member this.MaxInvites = this.maxInvites
    member this.NameMinLength = this.nameMinLength
    member this.NameMaxLength = this.nameMaxLength
    member this.InviteLifetime = this.inviteLifetime

[<RequireQualifiedAccess>]
module GuildLimits =
    let create maxGuilds maxGuildsPerPlayer maxMembers maxInvites nameMinLength nameMaxLength (inviteLifetime: TimeSpan) =
        let positive field value = if value < 1 then Error(DomainError.InvalidLimit(field, value)) else Ok value
        positive "MaxGuilds" maxGuilds
        |> Result.bind (fun _ -> positive "MaxGuildsPerPlayer" maxGuildsPerPlayer)
        |> Result.bind (fun _ -> if maxMembers < 2 then Error(DomainError.InvalidLimit("MaxMembers", maxMembers)) else Ok maxMembers)
        |> Result.bind (fun _ -> positive "MaxInvites" maxInvites)
        |> Result.bind (fun _ -> positive "NameMinLength" nameMinLength)
        |> Result.bind (fun _ ->
            if nameMaxLength < nameMinLength then Error(DomainError.InvalidLimit("NameMaxLength", nameMaxLength)) else Ok nameMaxLength)
        |> Result.bind (fun _ ->
            if inviteLifetime <= TimeSpan.Zero then Error(DomainError.InvalidLimit("InviteLifetime", int inviteLifetime.TotalSeconds))
            else Ok inviteLifetime)
        |> Result.map (fun _ -> {
            maxGuilds = maxGuilds
            maxGuildsPerPlayer = maxGuildsPerPlayer
            maxMembers = maxMembers
            maxInvites = maxInvites
            nameMinLength = nameMinLength
            nameMaxLength = nameMaxLength
            inviteLifetime = inviteLifetime
        })

[<RequireQualifiedAccess>]
type GuildError =
    /// Another guild has the name, in any case.
    | NameTaken
    /// The server has MaxGuilds guilds.
    | ServerFull
    /// The player is in MaxGuildsPerPlayer guilds.
    | PlayerLimit
    /// The guild has MaxMembers members.
    | GuildFull
    /// The guild has MaxInvites pending invitations.
    | InvitesFull
    /// No such guild, or the actor is not in it.
    | NotFound
    /// The target is not a member, or has no such invitation.
    | TargetNotFound
    | AlreadyMember
    | AlreadyInvited
    | NotPermitted
    /// The master hands the role over or disbands the guild before leaving.
    | MasterStays

/// One guild: its name, members and pending invitations. Owned by the guild
/// book's owner; no function exposes its live collections.
[<NoEquality; NoComparison>]
type Guild =
    private {
        id: GuildId
        name: GuildName
        createdAt: DateTimeOffset
        members: Dictionary<PlayerId, GuildMember>
        invites: Dictionary<PlayerId, GuildInvite>
    }

    member this.Id = this.id
    member this.Name = this.name
    member this.CreatedAt = this.createdAt
    member this.MemberCount = this.members.Count
    member this.Members = List.ofSeq this.members.Values
    member this.Invites = List.ofSeq this.invites.Values

    member this.Member player =
        match this.members.TryGetValue player with
        | true, membership -> ValueSome membership
        | false, _ -> ValueNone

    member this.Master = this.members.Values |> Seq.tryFind (fun membership -> membership.Role = GuildRole.Master) |> Option.toValueOption

/// What storage restores: a guild with its members and invitations.
type StoredGuild = {
    Id: GuildId
    Name: GuildName
    CreatedAt: DateTimeOffset
    Members: GuildMember list
    Invites: GuildInvite list
}

/// What a disbanded guild leaves: who was in it and who was invited.
type DisbandedGuild = {
    Guild: GuildId
    Name: GuildName
    Members: GuildMember list
    Invites: GuildInvite list
}

/// Every guild of the server and the indexes the limits need. Mutable and
/// owned by one agent; operations validate fully before changing anything.
[<NoEquality; NoComparison>]
type GuildBook =
    private {
        limits: GuildLimits
        guilds: Dictionary<GuildId, Guild>
        names: Dictionary<string, GuildId>
        memberships: Dictionary<PlayerId, HashSet<GuildId>>
        invitations: Dictionary<PlayerId, HashSet<GuildId>>
    }

    member this.Limits = this.limits
    member this.Count = this.guilds.Count

[<RequireQualifiedAccess>]
module GuildBook =
    let private index (table: Dictionary<PlayerId, HashSet<GuildId>>) player guild =
        match table.TryGetValue player with
        | true, set -> set.Add guild |> ignore
        | false, _ -> table[player] <- HashSet [ guild ]

    let private unindex (table: Dictionary<PlayerId, HashSet<GuildId>>) player guild =
        match table.TryGetValue player with
        | true, set ->
            set.Remove guild |> ignore
            if set.Count = 0 then table.Remove player |> ignore
        | false, _ -> ()

    let private indexed (table: Dictionary<PlayerId, HashSet<GuildId>>) player =
        match table.TryGetValue player with
        | true, set -> set.Count
        | false, _ -> 0

    let empty limits = {
        limits = limits
        guilds = Dictionary()
        names = Dictionary(StringComparer.Ordinal)
        memberships = Dictionary()
        invitations = Dictionary()
    }

    /// The guilds storage kept, as they are: limits lowered since bind nobody.
    let restore limits (stored: StoredGuild seq) =
        let book = empty limits
        for guild in stored do
            let entry = {
                id = guild.Id
                name = guild.Name
                createdAt = guild.CreatedAt
                members = Dictionary()
                invites = Dictionary()
            }
            for membership in guild.Members do
                entry.members[membership.Player] <- membership
                index book.memberships membership.Player guild.Id
            for invite in guild.Invites do
                entry.invites[invite.Player] <- invite
                index book.invitations invite.Player guild.Id
            book.guilds[guild.Id] <- entry
            book.names[GuildName.key guild.Name] <- guild.Id
        book

    /// Lowered or raised limits apply to the next actions only.
    let withLimits limits (book: GuildBook) = { book with limits = limits }

    let tryFind guild (book: GuildBook) =
        match book.guilds.TryGetValue guild with
        | true, entry -> ValueSome entry
        | false, _ -> ValueNone

    let all (book: GuildBook) = List.ofSeq book.guilds.Values

    let tryFindByName (name: string) (book: GuildBook) =
        match book.names.TryGetValue(name.Normalize(NormalizationForm.FormC).ToLowerInvariant()) with
        | true, guild -> tryFind guild book
        | false, _ -> ValueNone

    /// The guilds the player is in.
    let guildsOf player (book: GuildBook) =
        match book.memberships.TryGetValue player with
        | true, set -> set |> Seq.choose (fun guild -> tryFind guild book |> ValueOption.toOption) |> List.ofSeq
        | false, _ -> []

    /// The player's pending invitations, expired ones included until expireInvites runs.
    let invitesOf player (book: GuildBook) =
        match book.invitations.TryGetValue player with
        | true, set ->
            set
            |> Seq.choose (fun guild ->
                match book.guilds.TryGetValue guild with
                | true, entry ->
                    match entry.invites.TryGetValue player with
                    | true, invite -> Some invite
                    | false, _ -> None
                | false, _ -> None)
            |> List.ofSeq
        | false, _ -> []

    /// The actor's guild and membership: absent for both an unknown guild and
    /// one the actor is not in, so a stranger learns nothing about it.
    let private membership actor guild (book: GuildBook) =
        match tryFind guild book with
        | ValueSome entry ->
            match entry.Member actor with
            | ValueSome own -> Ok(entry, own)
            | ValueNone -> Error GuildError.NotFound
        | ValueNone -> Error GuildError.NotFound

    let private target player (entry: Guild) =
        match entry.Member player with
        | ValueSome membership -> Ok membership
        | ValueNone -> Error GuildError.TargetNotFound

    let private addMember (book: GuildBook) (entry: Guild) (membership: GuildMember) =
        entry.members[membership.Player] <- membership
        index book.memberships membership.Player entry.id

    let private dropMember (book: GuildBook) (entry: Guild) player =
        entry.members.Remove player |> ignore
        unindex book.memberships player entry.id

    let private dropInvite (book: GuildBook) (entry: Guild) player =
        entry.invites.Remove player |> ignore
        unindex book.invitations player entry.id

    /// A new guild with its creator as master. The ID comes from storage.
    let create id (name: GuildName) creator (now: DateTimeOffset) (book: GuildBook) =
        if book.names.ContainsKey(GuildName.key name) then Error GuildError.NameTaken
        elif book.guilds.Count >= book.limits.MaxGuilds then Error GuildError.ServerFull
        elif indexed book.memberships creator >= book.limits.MaxGuildsPerPlayer then Error GuildError.PlayerLimit
        else
            let entry = { id = id; name = name; createdAt = now; members = Dictionary(); invites = Dictionary() }
            book.guilds[id] <- entry
            book.names[GuildName.key name] <- id
            let master = { Player = creator; Role = GuildRole.Master; JoinedAt = now; Mute = ValueNone }
            addMember book entry master
            Ok(entry, master)

    /// An invitation from the master or an officer. A full guild or a player
    /// at the limit is refused now already: accepting would fail.
    let invite actor guild player (now: DateTimeOffset) (book: GuildBook) =
        membership actor guild book
        |> Result.bind (fun (entry, own) ->
            if not (GuildRole.mayInvite own.Role) then Error GuildError.NotPermitted
            elif entry.members.ContainsKey player then Error GuildError.AlreadyMember
            elif entry.invites.ContainsKey player then Error GuildError.AlreadyInvited
            elif entry.members.Count >= book.limits.MaxMembers then Error GuildError.GuildFull
            elif entry.invites.Count >= book.limits.MaxInvites then Error GuildError.InvitesFull
            elif indexed book.memberships player >= book.limits.MaxGuildsPerPlayer then Error GuildError.PlayerLimit
            else
                let invitation = {
                    Guild = guild
                    Player = player
                    InvitedBy = actor
                    CreatedAt = now
                    Expires = now + book.limits.InviteLifetime
                }
                entry.invites[player] <- invitation
                index book.invitations player guild
                Ok invitation)

    let private invitation player guild (now: DateTimeOffset) (book: GuildBook) =
        match tryFind guild book with
        | ValueSome entry ->
            match entry.invites.TryGetValue player with
            | true, invite when now < invite.Expires -> Ok(entry, invite)
            | true, _ | false, _ -> Error GuildError.TargetNotFound
        | ValueNone -> Error GuildError.TargetNotFound

    /// Joining checks the limits at this moment, not when the invitation was sent.
    let accept player guild (now: DateTimeOffset) (book: GuildBook) =
        invitation player guild now book
        |> Result.bind (fun (entry, invite) ->
            if entry.members.Count >= book.limits.MaxMembers then Error GuildError.GuildFull
            elif indexed book.memberships player >= book.limits.MaxGuildsPerPlayer then Error GuildError.PlayerLimit
            else
                dropInvite book entry player
                let joined = { Player = player; Role = GuildRole.Member; JoinedAt = now; Mute = ValueNone }
                addMember book entry joined
                Ok(entry, invite, joined))

    let decline player guild (now: DateTimeOffset) (book: GuildBook) =
        invitation player guild now book
        |> Result.map (fun (entry, invite) ->
            dropInvite book entry player
            invite)

    let leave player guild (book: GuildBook) =
        membership player guild book
        |> Result.bind (fun (entry, own) ->
            if own.Role = GuildRole.Master then Error GuildError.MasterStays
            else
                dropMember book entry player
                Ok(entry, own))

    /// Exclusion by the master or an officer, above the target's role.
    let exclude actor guild player (book: GuildBook) =
        membership actor guild book
        |> Result.bind (fun (entry, own) ->
            target player entry
            |> Result.bind (fun excluded ->
                if not (GuildRole.outranks own.Role excluded.Role) then Error GuildError.NotPermitted
                else
                    dropMember book entry player
                    Ok(entry, excluded)))

    /// The master makes a member an officer or an officer a member again.
    let setRole actor guild player role (book: GuildBook) =
        membership actor guild book
        |> Result.bind (fun (entry, own) ->
            target player entry
            |> Result.bind (fun current ->
                if own.Role <> GuildRole.Master || current.Role = GuildRole.Master || role = GuildRole.Master then
                    Error GuildError.NotPermitted
                else
                    let changed = { current with Role = role }
                    entry.members[player] <- changed
                    Ok(entry, changed)))

    let private handOver (entry: Guild) player =
        target player entry
        |> Result.map (fun next ->
            let previous =
                entry.Master
                |> ValueOption.filter (fun master -> master.Player <> player)
                |> ValueOption.map (fun master ->
                    let officer = { master with Role = GuildRole.Officer }
                    entry.members[master.Player] <- officer
                    officer)
            let master = { next with Role = GuildRole.Master }
            entry.members[player] <- master
            struct (previous, master))

    /// The master hands the role to a member and becomes an officer. The new
    /// master is in the guild already, so no limit applies.
    let transfer actor guild player (book: GuildBook) =
        membership actor guild book
        |> Result.bind (fun (entry, own) ->
            if own.Role <> GuildRole.Master || player = actor then Error GuildError.NotPermitted
            else handOver entry player |> Result.map (fun (struct (previous, master)) -> entry, previous, master))

    /// An administrator appoints a member when the master is banned or gone;
    /// a master still in the guild becomes an officer.
    let appoint guild player (book: GuildBook) =
        match tryFind guild book with
        | ValueNone -> Error GuildError.NotFound
        | ValueSome entry -> handOver entry player |> Result.map (fun (struct (previous, master)) -> entry, previous, master)

    /// A guild mute by the master or an officer, above the target's role.
    let mute actor guild player term reason (now: DateTimeOffset) (book: GuildBook) =
        membership actor guild book
        |> Result.bind (fun (entry, own) ->
            target player entry
            |> Result.bind (fun current ->
                if not (GuildRole.outranks own.Role current.Role) then Error GuildError.NotPermitted
                else
                    let mute = { Reason = reason; IssuedBy = actor; IssuedAt = now; Expires = Sanction.expiry now term }
                    let changed = { current with Mute = ValueSome mute }
                    entry.members[player] <- changed
                    Ok(entry, changed)))

    let unmute actor guild player (now: DateTimeOffset) (book: GuildBook) =
        membership actor guild book
        |> Result.bind (fun (entry, own) ->
            target player entry
            |> Result.bind (fun current ->
                if not (GuildRole.outranks own.Role current.Role) then Error GuildError.NotPermitted
                elif (GuildMember.mute now current).IsNone then Error GuildError.TargetNotFound
                else
                    let changed = { current with Mute = ValueNone }
                    entry.members[player] <- changed
                    Ok(entry, changed)))

    let private remove (book: GuildBook) (entry: Guild) =
        let disbanded = { Guild = entry.id; Name = entry.name; Members = entry.Members; Invites = entry.Invites }
        for membership in disbanded.Members do unindex book.memberships membership.Player entry.id
        for invite in disbanded.Invites do unindex book.invitations invite.Player entry.id
        book.guilds.Remove entry.id |> ignore
        book.names.Remove(GuildName.key entry.name) |> ignore
        disbanded

    /// The master disbands the guild; its name is free again.
    let disband actor guild (book: GuildBook) =
        membership actor guild book
        |> Result.bind (fun (entry, own) ->
            if own.Role <> GuildRole.Master then Error GuildError.NotPermitted else Ok(remove book entry))

    /// An administrator disbands a guild, for one when its name breaks the rules.
    let dissolve guild (book: GuildBook) =
        match tryFind guild book with
        | ValueSome entry -> Ok(remove book entry)
        | ValueNone -> Error GuildError.NotFound

    /// Removes and returns the invitations that expired by now.
    let expireInvites (now: DateTimeOffset) (book: GuildBook) =
        let expired =
            book.guilds.Values
            |> Seq.collect (fun entry -> entry.invites.Values)
            |> Seq.filter (fun invite -> now >= invite.Expires)
            |> List.ofSeq
        for invite in expired do dropInvite book book.guilds[invite.Guild] invite.Player
        expired

    /// Clears and returns the guild mutes that ended by now, with their guilds.
    let expireMutes (now: DateTimeOffset) (book: GuildBook) =
        [ for entry in book.guilds.Values do
              for membership in List.ofSeq entry.members.Values do
                  match membership.Mute with
                  | ValueSome mute when not (GuildMute.activeAt now mute) ->
                      let changed = { membership with Mute = ValueNone }
                      entry.members[membership.Player] <- changed
                      yield entry, changed
                  | ValueSome _ | ValueNone -> () ]

    /// The member may write in the guild chat at now: in the guild and not muted there.
    let mayWrite player guild (now: DateTimeOffset) (book: GuildBook) =
        match tryFind guild book with
        | ValueSome entry ->
            match entry.Member player with
            | ValueSome membership when (GuildMember.mute now membership).IsNone -> Ok membership
            | ValueSome _ -> Error GuildError.NotPermitted
            | ValueNone -> Error GuildError.NotFound
        | ValueNone -> Error GuildError.NotFound

    /// Removing a message of the guild chat: the master any, an officer a
    /// member's; an author no longer in the guild ranks as a member.
    /// Authors do not remove their own messages.
    let mayRemoveMessage actor guild (author: PlayerId voption) (book: GuildBook) =
        membership actor guild book
        |> Result.bind (fun (entry, own) ->
            let authorRole =
                author
                |> ValueOption.bind entry.Member
                |> ValueOption.map _.Role
                |> ValueOption.defaultValue GuildRole.Member
            if own.Role = GuildRole.Master || GuildRole.outranks own.Role authorRole then Ok entry
            else Error GuildError.NotPermitted)
