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

    [<Measure>]
    type guildTitleName

    [<Measure>]
    type guildTitleId

    [<Measure>]
    type guildInfo

type GuildName = string<guildName>

/// A custom role's name (docs/GuildsRu.md): the same rules as a guild name.
type GuildTitleName = string<guildTitleName>

/// Storage-issued and never reused across guilds.
type GuildTitleId = uint64<guildTitleId>

/// What the master tells about the guild (docs/GuildsRu.md): the chat text rules.
type GuildInfo = string<guildInfo>

/// The rules guild names and custom role names share.
module internal GuildText =
    let private lettersDigitsAndSpaces (source: string) =
        if source.EnumerateRunes() |> Seq.forall (fun rune -> Rune.IsLetterOrDigit rune || rune.Value = int ' ') then ValueNone
        else ValueSome TextError.InvalidCharacters

    // Runs of spaces become one, so "Два  слова" cannot sit beside "Два слова".
    let private canonical (source: string) =
        let trimmed = PrimitiveValidation.nfcTrim source
        if trimmed.Contains "  " then RegularExpressions.Regex.Replace(trimmed, " {2,}", " ") else trimmed

    /// Letters and digits of any alphabet and single spaces between words, in
    /// NFC: no other whitespace, punctuation or controls; minLength..maxLength
    /// scalar values, spaces included.
    let name field minLength maxLength raw : Result<string, DomainError> =
        if minLength <= 0 || minLength > maxLength then Error(DomainError.InvalidLimit(field, minLength))
        else
            PrimitiveValidation.text field maxLength canonical false lettersDigitsAndSpaces raw
            |> Result.bind (fun canonical ->
                if PrimitiveValidation.scalarCount canonical < minLength then
                    Error(DomainError.InvalidText(field, TextError.TooShort minLength))
                else Ok canonical)

    /// Unique regardless of case: "Стражи" and "стражи" are one name.
    let key (text: string) = text.ToLowerInvariant()

[<RequireQualifiedAccess>]
module GuildName =
    /// The longest guild name a server may allow.
    [<Literal>]
    let MaxLength = 64

    let value (name: GuildName) : string = UMX.untag name

    /// Letters, digits and single spaces between words (GuildText.name). The
    /// word list and uniqueness are the owner's checks.
    let create minLength maxLength raw : Result<GuildName, DomainError> =
        GuildText.name "GuildName" minLength maxLength raw |> Result.map UMX.tag<guildName>

    /// Names are unique regardless of case: "Стражи" and "стражи" are one name.
    let key (name: GuildName) = GuildText.key (value name)

[<RequireQualifiedAccess>]
module GuildTitleName =
    let value (name: GuildTitleName) : string = UMX.untag name

    /// The rules of a guild name; the word list and uniqueness inside the
    /// guild are the owner's checks.
    let create minLength maxLength raw : Result<GuildTitleName, DomainError> =
        GuildText.name "GuildTitleName" minLength maxLength raw |> Result.map UMX.tag<guildTitleName>

    let key (name: GuildTitleName) = GuildText.key (value name)

[<RequireQualifiedAccess>]
module GuildTitleId =
    let value (id: GuildTitleId) : uint64 = UMX.untag id

    /// A storage row ID: positive and within SQLite's INTEGER.
    let create raw : Result<GuildTitleId, DomainError> =
        if raw = 0UL || raw > uint64 Int64.MaxValue then Error(DomainError.InvalidId "GuildTitleId") else Ok(UMX.tag<guildTitleId> raw)

[<RequireQualifiedAccess>]
module GuildInfo =
    let value (info: GuildInfo) : string = UMX.untag info

    /// The chat text rules: kept as written, line breaks and tabs allowed,
    /// other controls and blank text refused. The word list is the owner's check.
    let create maxLength raw : Result<GuildInfo, DomainError> =
        PrimitiveValidation.text "GuildInfo" maxLength id true PrimitiveValidation.unrestricted raw
        |> Result.map UMX.tag<guildInfo>

/// A custom role of one guild, such as "Казначей": cosmetic, it grants no
/// rights; the master creates and assigns it.
type GuildTitle = {
    Id: GuildTitleId
    Name: GuildTitleName
}

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
    /// The member's custom role, one of the guild's; absent: none.
    Title: GuildTitleId voption
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

/// A player's request to join a guild from the catalog. The master or an
/// officer answers it; accepting checks the limits at that moment.
type GuildJoinRequest = {
    Guild: GuildId
    Player: PlayerId
    CreatedAt: DateTimeOffset
    Expires: DateTimeOffset
}

/// The values of the server's guild limits, before GuildLimits.create checks them.
type GuildLimitSettings = {
    MaxGuilds: int
    /// Guilds a player is in, the ones they master included.
    MaxGuildsPerPlayer: int
    MaxMembers: int
    /// Pending invitations of one guild.
    MaxInvites: int
    /// Bounds of a guild name and of a custom role's name.
    NameMinLength: int
    NameMaxLength: int
    InviteLifetime: TimeSpan
    /// Custom roles of one guild; 0 leaves guilds without them.
    MaxTitles: int
    /// Bound of the guild information in Unicode scalar values.
    InfoMaxLength: int
    /// Pending join requests of one guild.
    MaxJoinRequests: int
    /// Pending join requests of one player, so that nobody floods every guild.
    MaxApplicationsPerPlayer: int
    JoinRequestLifetime: TimeSpan
}

/// The server's guild limits (docs/DomainSpecRu.MD §10), checked when an
/// action would grow what they bound.
type GuildLimits =
    private { settings: GuildLimitSettings }

    member this.MaxGuilds = this.settings.MaxGuilds
    member this.MaxGuildsPerPlayer = this.settings.MaxGuildsPerPlayer
    member this.MaxMembers = this.settings.MaxMembers
    member this.MaxInvites = this.settings.MaxInvites
    member this.NameMinLength = this.settings.NameMinLength
    member this.NameMaxLength = this.settings.NameMaxLength
    member this.InviteLifetime = this.settings.InviteLifetime
    member this.MaxTitles = this.settings.MaxTitles
    member this.InfoMaxLength = this.settings.InfoMaxLength
    member this.MaxJoinRequests = this.settings.MaxJoinRequests
    member this.MaxApplicationsPerPlayer = this.settings.MaxApplicationsPerPlayer
    member this.JoinRequestLifetime = this.settings.JoinRequestLifetime

[<RequireQualifiedAccess>]
module GuildLimits =
    let create (settings: GuildLimitSettings) =
        let invalid field (value: int) = Error(DomainError.InvalidLimit(field, value))
        let positive field value = if value < 1 then invalid field value else Ok()
        let lifetime field (value: TimeSpan) = if value <= TimeSpan.Zero then invalid field (int value.TotalSeconds) else Ok()
        positive "MaxGuilds" settings.MaxGuilds
        |> Result.bind (fun () -> positive "MaxGuildsPerPlayer" settings.MaxGuildsPerPlayer)
        |> Result.bind (fun () -> if settings.MaxMembers < 2 then invalid "MaxMembers" settings.MaxMembers else Ok())
        |> Result.bind (fun () -> positive "MaxInvites" settings.MaxInvites)
        |> Result.bind (fun () -> positive "NameMinLength" settings.NameMinLength)
        |> Result.bind (fun () ->
            if settings.NameMaxLength < settings.NameMinLength then invalid "NameMaxLength" settings.NameMaxLength else Ok())
        |> Result.bind (fun () -> lifetime "InviteLifetime" settings.InviteLifetime)
        |> Result.bind (fun () -> if settings.MaxTitles < 0 then invalid "MaxTitles" settings.MaxTitles else Ok())
        |> Result.bind (fun () -> positive "InfoMaxLength" settings.InfoMaxLength)
        |> Result.bind (fun () -> positive "MaxJoinRequests" settings.MaxJoinRequests)
        |> Result.bind (fun () -> positive "MaxApplicationsPerPlayer" settings.MaxApplicationsPerPlayer)
        |> Result.bind (fun () -> lifetime "JoinRequestLifetime" settings.JoinRequestLifetime)
        |> Result.map (fun () -> { settings = settings })

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
    /// Another custom role of the guild has the name, in any case.
    | TitleNameTaken
    /// The guild has MaxTitles custom roles.
    | TitlesFull
    /// The guild has no such custom role.
    | TitleNotFound
    /// The guild has MaxJoinRequests pending join requests.
    | JoinRequestsFull
    /// The player asked to join the guild already.
    | AlreadyApplied
    /// The player has MaxApplicationsPerPlayer pending join requests.
    | ApplicationLimit

/// One guild: its name, members, custom roles, pending invitations and join
/// requests. Owned by the guild book's owner; no function exposes its live collections.
[<NoEquality; NoComparison>]
type Guild =
    private {
        id: GuildId
        name: GuildName
        createdAt: DateTimeOffset
        members: Dictionary<PlayerId, GuildMember>
        invites: Dictionary<PlayerId, GuildInvite>
        titles: Dictionary<GuildTitleId, GuildTitle>
        requests: Dictionary<PlayerId, GuildJoinRequest>
        mutable info: GuildInfo voption
        mutable listed: bool
    }

    member this.Id = this.id
    member this.Name = this.name
    /// Absent: the master wrote none.
    member this.Info = this.info
    /// Shown in the public catalog, where players ask to join.
    member this.Listed = this.listed
    member this.CreatedAt = this.createdAt
    member this.MemberCount = this.members.Count
    member this.Members = List.ofSeq this.members.Values
    member this.Invites = List.ofSeq this.invites.Values
    /// Oldest first: IDs grow with creation.
    member this.Titles = this.titles.Values |> Seq.sortBy _.Id |> List.ofSeq
    /// Pending join requests, oldest first.
    member this.JoinRequests = this.requests.Values |> Seq.sortBy _.CreatedAt |> List.ofSeq

    member this.Member player =
        match this.members.TryGetValue player with
        | true, membership -> ValueSome membership
        | false, _ -> ValueNone

    member this.Master = this.members.Values |> Seq.tryFind (fun membership -> membership.Role = GuildRole.Master) |> Option.toValueOption

/// What storage restores: a guild with its members, custom roles, invitations and join requests.
type StoredGuild = {
    Id: GuildId
    Name: GuildName
    CreatedAt: DateTimeOffset
    Members: GuildMember list
    Invites: GuildInvite list
    Titles: GuildTitle list
    Info: GuildInfo voption
    Listed: bool
    JoinRequests: GuildJoinRequest list
}

/// What a disbanded guild leaves: who was in it, who was invited and who asked to join.
type DisbandedGuild = {
    Guild: GuildId
    Name: GuildName
    Members: GuildMember list
    Invites: GuildInvite list
    JoinRequests: GuildJoinRequest list
}

[<RequireQualifiedAccess>]
type GuildCatalogSort =
    /// By name, regardless of case.
    | Name
    /// The largest first, then by name.
    | Members

/// The last entry a catalog page ended with; the next page starts after it.
/// NameKey is the guild name in lower case.
type GuildCatalogCursor = {
    Guild: GuildId
    NameKey: string
    Members: int
}

/// A search of the public catalog: listed guilds whose name contains Search
/// (lower case, empty: any) with MinMembers..MaxMembers members, only those
/// with free places when OpenOnly.
type GuildCatalogQuery = {
    Search: string
    MinMembers: int
    MaxMembers: int voption
    OpenOnly: bool
    Sort: GuildCatalogSort
    After: GuildCatalogCursor voption
}

/// One page of the catalog; Total counts every match, More says a next page exists.
type GuildCatalogPage = {
    Guilds: Guild list
    More: bool
    Total: int
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
        applications: Dictionary<PlayerId, HashSet<GuildId>>
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
        applications = Dictionary()
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
                titles = Dictionary()
                requests = Dictionary()
                info = guild.Info
                listed = guild.Listed
            }
            for title in guild.Titles do
                entry.titles[title.Id] <- title
            for membership in guild.Members do
                entry.members[membership.Player] <- membership
                index book.memberships membership.Player guild.Id
            for invite in guild.Invites do
                entry.invites[invite.Player] <- invite
                index book.invitations invite.Player guild.Id
            for request in guild.JoinRequests do
                entry.requests[request.Player] <- request
                index book.applications request.Player guild.Id
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

    /// The player's pending join requests, expired ones included until expireJoinRequests runs.
    let applicationsOf player (book: GuildBook) =
        match book.applications.TryGetValue player with
        | true, set ->
            set
            |> Seq.choose (fun guild ->
                match book.guilds.TryGetValue guild with
                | true, entry ->
                    match entry.requests.TryGetValue player with
                    | true, request -> Some request
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

    // The player's join request to the guild, removed if there was one.
    let private dropRequest (book: GuildBook) (entry: Guild) player =
        match entry.requests.TryGetValue player with
        | true, request ->
            entry.requests.Remove player |> ignore
            unindex book.applications player entry.id
            ValueSome request
        | false, _ -> ValueNone

    /// A new guild with its creator as master. The ID comes from storage.
    let create id (name: GuildName) creator (now: DateTimeOffset) (book: GuildBook) =
        if book.names.ContainsKey(GuildName.key name) then Error GuildError.NameTaken
        elif book.guilds.Count >= book.limits.MaxGuilds then Error GuildError.ServerFull
        elif indexed book.memberships creator >= book.limits.MaxGuildsPerPlayer then Error GuildError.PlayerLimit
        else
            let entry = {
                id = id
                name = name
                createdAt = now
                members = Dictionary()
                invites = Dictionary()
                titles = Dictionary()
                requests = Dictionary()
                info = ValueNone
                listed = true
            }
            book.guilds[id] <- entry
            book.names[GuildName.key name] <- id
            let master = { Player = creator; Role = GuildRole.Master; JoinedAt = now; Mute = ValueNone; Title = ValueNone }
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

    // A new member, from an invitation or a join request: the other one goes too.
    let private join (book: GuildBook) (entry: Guild) player (now: DateTimeOffset) =
        dropInvite book entry player
        let request = dropRequest book entry player
        let joined = { Player = player; Role = GuildRole.Member; JoinedAt = now; Mute = ValueNone; Title = ValueNone }
        addMember book entry joined
        struct (joined, request)

    let private roomFor (book: GuildBook) (entry: Guild) player =
        if entry.members.Count >= book.limits.MaxMembers then Error GuildError.GuildFull
        elif indexed book.memberships player >= book.limits.MaxGuildsPerPlayer then Error GuildError.PlayerLimit
        else Ok()

    /// Joining checks the limits at this moment, not when the invitation was
    /// sent. A join request of the player to the guild goes as well (returned).
    let accept player guild (now: DateTimeOffset) (book: GuildBook) =
        invitation player guild now book
        |> Result.bind (fun (entry, invite) ->
            roomFor book entry player
            |> Result.map (fun () ->
                let struct (joined, request) = join book entry player now
                entry, invite, joined, request))

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

    let private mastered actor guild (book: GuildBook) =
        membership actor guild book
        |> Result.bind (fun (entry, own) -> if own.Role = GuildRole.Master then Ok entry else Error GuildError.NotPermitted)

    let private title id (entry: Guild) =
        match entry.titles.TryGetValue id with
        | true, found -> Ok found
        | false, _ -> Error GuildError.TitleNotFound

    // Another custom role of the guild has the name in any case; except is the renamed one.
    let private titleNameTaken (entry: Guild) (name: GuildTitleName) (except: GuildTitleId voption) =
        let key = GuildTitleName.key name
        entry.titles.Values |> Seq.exists (fun other -> ValueSome other.Id <> except && GuildTitleName.key other.Name = key)

    /// The master creates a custom role. The ID comes from storage.
    let createTitle actor guild id (name: GuildTitleName) (book: GuildBook) =
        mastered actor guild book
        |> Result.bind (fun entry ->
            if entry.titles.Count >= book.limits.MaxTitles then Error GuildError.TitlesFull
            elif titleNameTaken entry name ValueNone then Error GuildError.TitleNameTaken
            else
                let created = { Id = id; Name = name }
                entry.titles[id] <- created
                Ok(entry, created))

    /// The master renames a custom role; its holders keep it.
    let renameTitle actor guild id (name: GuildTitleName) (book: GuildBook) =
        mastered actor guild book
        |> Result.bind (fun entry ->
            title id entry
            |> Result.bind (fun current ->
                if titleNameTaken entry name (ValueSome id) then Error GuildError.TitleNameTaken
                else
                    let renamed = { current with Name = name }
                    entry.titles[id] <- renamed
                    Ok(entry, renamed)))

    /// The master deletes a custom role; the members who held it are returned without it.
    let deleteTitle actor guild id (book: GuildBook) =
        mastered actor guild book
        |> Result.bind (fun entry ->
            title id entry
            |> Result.map (fun _ ->
                entry.titles.Remove id |> ignore
                let holders = entry.members.Values |> Seq.filter (fun membership -> membership.Title = ValueSome id) |> List.ofSeq
                let cleared = holders |> List.map (fun membership -> { membership with Title = ValueNone })
                for membership in cleared do entry.members[membership.Player] <- membership
                entry, cleared))

    /// The master gives a member one of the guild's custom roles, replacing
    /// the one they had, or takes it away (ValueNone). The master may hold one too.
    let setTitle actor guild player (id: GuildTitleId voption) (book: GuildBook) =
        mastered actor guild book
        |> Result.bind (fun entry ->
            let known =
                match id with
                | ValueSome titleId -> title titleId entry |> Result.map ignore
                | ValueNone -> Ok()
            known
            |> Result.bind (fun () -> target player entry)
            |> Result.map (fun current ->
                let changed = { current with Title = id }
                entry.members[player] <- changed
                entry, changed))

    /// The master writes the guild information or clears it (ValueNone).
    let setInfo actor guild (info: GuildInfo voption) (book: GuildBook) =
        mastered actor guild book
        |> Result.map (fun entry ->
            entry.info <- info
            entry)

    /// The master shows the guild in the public catalog or hides it; pending
    /// join requests stay either way.
    let setListed actor guild listed (book: GuildBook) =
        mastered actor guild book
        |> Result.map (fun entry ->
            entry.listed <- listed
            entry)

    /// A player asks to join a listed guild. A guild hidden from the catalog
    /// is not found; the limits that would refuse the answer refuse now already.
    let apply player guild (now: DateTimeOffset) (book: GuildBook) =
        match tryFind guild book with
        | ValueSome entry when entry.listed ->
            if entry.members.ContainsKey player then Error GuildError.AlreadyMember
            elif entry.requests.ContainsKey player then Error GuildError.AlreadyApplied
            elif entry.invites.ContainsKey player then Error GuildError.AlreadyInvited
            elif indexed book.applications player >= book.limits.MaxApplicationsPerPlayer then Error GuildError.ApplicationLimit
            elif entry.requests.Count >= book.limits.MaxJoinRequests then Error GuildError.JoinRequestsFull
            else
                roomFor book entry player
                |> Result.map (fun () ->
                    let request = { Guild = guild; Player = player; CreatedAt = now; Expires = now + book.limits.JoinRequestLifetime }
                    entry.requests[player] <- request
                    index book.applications player guild
                    entry, request)
        | ValueSome _ | ValueNone -> Error GuildError.NotFound

    /// The player takes their join request back.
    let withdraw player guild (book: GuildBook) =
        match tryFind guild book with
        | ValueSome entry ->
            match dropRequest book entry player with
            | ValueSome request -> Ok(entry, request)
            | ValueNone -> Error GuildError.TargetNotFound
        | ValueNone -> Error GuildError.TargetNotFound

    // A pending join request the master or an officer answers.
    let private answerable actor guild player (now: DateTimeOffset) (book: GuildBook) =
        membership actor guild book
        |> Result.bind (fun (entry, own) ->
            if not (GuildRole.mayInvite own.Role) then Error GuildError.NotPermitted
            else
                match entry.requests.TryGetValue player with
                | true, request when now < request.Expires -> Ok(entry, request)
                | true, _ | false, _ -> Error GuildError.TargetNotFound)

    /// The master or an officer lets the player in; the limits apply at this moment.
    let acceptRequest actor guild player (now: DateTimeOffset) (book: GuildBook) =
        answerable actor guild player now book
        |> Result.bind (fun (entry, request) ->
            roomFor book entry player
            |> Result.map (fun () ->
                let invited = entry.invites.ContainsKey player
                let struct (joined, _) = join book entry player now
                entry, request, joined, invited))

    let rejectRequest actor guild player (now: DateTimeOffset) (book: GuildBook) =
        answerable actor guild player now book
        |> Result.map (fun (entry, request) ->
            dropRequest book entry player |> ignore
            entry, request)

    /// The guilds the catalog matches, ordered by the query, after its cursor.
    let catalog (query: GuildCatalogQuery) pageSize (book: GuildBook) =
        let maxMembers = book.limits.MaxMembers
        let matches (entry: Guild) =
            let count = entry.members.Count
            entry.listed
            && count >= query.MinMembers
            && (query.MaxMembers |> ValueOption.forall (fun maximum -> count <= maximum))
            && (not query.OpenOnly || count < maxMembers)
            && (query.Search.Length = 0 || (GuildName.key entry.name).Contains(query.Search, StringComparison.Ordinal))
        // Ordinal comparison of the struct keys; the guild ID breaks ties.
        let key (members: int) nameKey (id: GuildId) =
            match query.Sort with
            | GuildCatalogSort.Name -> struct (0, nameKey, id)
            | GuildCatalogSort.Members -> struct (-members, nameKey, id)
        let keyOf (entry: Guild) = key entry.members.Count (GuildName.key entry.name) entry.id
        let found = book.guilds.Values |> Seq.filter matches |> Seq.sortBy keyOf |> Array.ofSeq
        let start =
            match query.After with
            | ValueSome after ->
                let last = key after.Members after.NameKey after.Guild
                found |> Array.tryFindIndex (fun entry -> keyOf entry > last) |> Option.defaultValue found.Length
            | ValueNone -> 0
        let taken = min pageSize (found.Length - start)
        { Guilds = List.ofArray found[start .. start + taken - 1]
          More = start + taken < found.Length
          Total = found.Length }

    let private remove (book: GuildBook) (entry: Guild) =
        let disbanded = { Guild = entry.id; Name = entry.name; Members = entry.Members; Invites = entry.Invites; JoinRequests = entry.JoinRequests }
        for membership in disbanded.Members do unindex book.memberships membership.Player entry.id
        for invite in disbanded.Invites do unindex book.invitations invite.Player entry.id
        for request in disbanded.JoinRequests do unindex book.applications request.Player entry.id
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

    /// Removes and returns the join requests that expired by now.
    let expireJoinRequests (now: DateTimeOffset) (book: GuildBook) =
        let expired =
            book.guilds.Values
            |> Seq.collect (fun entry -> entry.requests.Values)
            |> Seq.filter (fun request -> now >= request.Expires)
            |> List.ofSeq
        for request in expired do dropRequest book book.guilds[request.Guild] request.Player |> ignore
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
