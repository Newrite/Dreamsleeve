module Dreamsleeve.Server.Tests.GuildDomainTests

open System
open Expecto
open Dreamsleeve.Server.Domain

let private ok = function Ok value -> value | Error error -> failtestf "Expected success, got %A" error
let private refused expected result =
    match result with
    | Error error -> Expect.equal error expected "refusal"
    | Ok value -> failtestf "Expected %A, got %A" expected value
let private pid raw = PlayerId.create raw |> ok
let private gid raw = GuildId.create raw |> ok
let private name text = GuildName.create 3 24 text |> ok
let private at minutes = DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero).AddMinutes(float minutes)
let private reason = SanctionReason.create "Флуд" |> ok
let private maxTitles = 2
let private limits maxGuilds perPlayer members invites =
    GuildLimits.create maxGuilds perPlayer members invites 3 24 (TimeSpan.FromDays 7.0) maxTitles 100 |> ok
let private tid raw = GuildTitleId.create raw |> ok
let private titleName text = GuildTitleName.create 3 24 text |> ok
let private book () = GuildBook.empty (limits 10 3 4 4)

// master 1, officer 2, member 3 in guild 1.
let private staffed () =
    let guilds = book ()
    GuildBook.create (gid 1UL) (name "Стражи") (pid 1UL) (at 0) guilds |> ok |> ignore
    for player in [ 2UL; 3UL ] do
        GuildBook.invite (pid 1UL) (gid 1UL) (pid player) (at 1) guilds |> ok |> ignore
        GuildBook.accept (pid player) (gid 1UL) (at 2) guilds |> ok |> ignore
    GuildBook.setRole (pid 1UL) (gid 1UL) (pid 2UL) GuildRole.Officer guilds |> ok |> ignore
    guilds

let private role guilds player =
    (GuildBook.tryFind (gid 1UL) guilds).Value.Member (pid player) |> ValueOption.map _.Role

let private titleOf guilds player =
    (GuildBook.tryFind (gid 1UL) guilds).Value.Member (pid player) |> ValueOption.bind _.Title

let tests = testList "Guild domain" [
    testCase "a guild name has letters, digits and spaces of any alphabet and is unique regardless of case" <| fun _ ->
        Expect.equal (GuildName.value (name "Стражи2")) "Стражи2" "Cyrillic letters and digits"
        Expect.equal (GuildName.value (name "  Guardians ")) "Guardians" "trimmed"
        Expect.equal (GuildName.value (name "Два слова")) "Два слова" "a space between words"
        Expect.equal (GuildName.value (name "Два   слова")) "Два слова" "a run of spaces is one space"
        for invalid in [ "Стражи!"; "a-b"; "tab\tname"; "nbsp\u00A0name"; "line\nname"; "" ] do
            Expect.isError (GuildName.create 3 24 invalid) $"refused: {invalid}"
        Expect.equal (GuildName.create 3 24 "Ab") (Error(DomainError.InvalidText("GuildName", TextError.TooShort 3))) "too short"
        Expect.equal (GuildName.create 3 5 "Abcdef") (Error(DomainError.InvalidText("GuildName", TextError.TooLong 5))) "too long"
        Expect.equal (GuildName.key (name "СТРАЖИ")) (GuildName.key (name "стражи")) "one name in any case"

    testCase "a guild channel ID names its guild and never a server-wide channel" <| fun _ ->
        let channel = ChatChannels.ofGuild (gid 7UL)
        Expect.equal (ChatChannels.classify channel) (ValueSome(struct (ChatChannelKind.Guild, ValueSome(gid 7UL)))) "guild channel"
        Expect.equal (ChatChannels.kindOf ChatChannels.globalId) (ValueSome ChatChannelKind.Global) "global"
        Expect.equal (ChatChannels.kindOf ChatChannels.systemId) (ValueSome ChatChannelKind.System) "system"
        Expect.equal (ChatChannels.kindOf (ChatChannelId.create 3UL |> ok)) ValueNone "no channel"
        Expect.isError (Chat.create channel ChatChannelKind.Global 10) "a guild ID is not the global channel"
        Expect.isOk (Chat.create channel ChatChannelKind.Guild 10) "a guild chat"

    testCase "creating checks the name, the server limit and the creator's guilds, own ones included" <| fun _ ->
        let guilds = GuildBook.empty (limits 3 2 4 4)
        let guild, master = GuildBook.create (gid 1UL) (name "Стражи") (pid 1UL) (at 0) guilds |> ok
        Expect.equal master.Role GuildRole.Master "the creator is the master"
        Expect.equal guild.MemberCount 1 "and a member"
        GuildBook.create (gid 2UL) (name "СТРАЖИ") (pid 2UL) (at 0) guilds |> refused GuildError.NameTaken
        GuildBook.create (gid 2UL) (name "Вороны") (pid 1UL) (at 0) guilds |> ok |> ignore
        GuildBook.create (gid 3UL) (name "Совы") (pid 1UL) (at 0) guilds |> refused GuildError.PlayerLimit
        GuildBook.create (gid 3UL) (name "Совы") (pid 2UL) (at 0) guilds |> ok |> ignore
        GuildBook.create (gid 4UL) (name "Лисы") (pid 3UL) (at 0) guilds |> refused GuildError.ServerFull
        let gone = GuildBook.disband (pid 1UL) (gid 1UL) guilds |> ok
        Expect.equal gone.Members.Length 1 "the master was in it"
        GuildBook.create (gid 4UL) (name "стражи") (pid 3UL) (at 0) guilds |> ok |> ignore

    testCase "officers and the master invite; joining checks the limits when the invitation is accepted" <| fun _ ->
        let guilds = staffed ()
        GuildBook.invite (pid 3UL) (gid 1UL) (pid 4UL) (at 3) guilds |> refused GuildError.NotPermitted
        GuildBook.invite (pid 2UL) (gid 1UL) (pid 4UL) (at 3) guilds |> ok |> ignore
        GuildBook.invite (pid 2UL) (gid 1UL) (pid 4UL) (at 3) guilds |> refused GuildError.AlreadyInvited
        GuildBook.invite (pid 2UL) (gid 1UL) (pid 3UL) (at 3) guilds |> refused GuildError.AlreadyMember
        GuildBook.invite (pid 1UL) (gid 1UL) (pid 5UL) (at 3) guilds |> ok |> ignore
        // A stranger learns nothing about a guild: it is not found.
        GuildBook.invite (pid 9UL) (gid 1UL) (pid 6UL) (at 3) guilds |> refused GuildError.NotFound
        GuildBook.accept (pid 4UL) (gid 1UL) (at 4) guilds |> ok |> ignore
        // The guild is full (4) when the second invitation is accepted.
        GuildBook.accept (pid 5UL) (gid 1UL) (at 4) guilds |> refused GuildError.GuildFull
        GuildBook.invite (pid 1UL) (gid 1UL) (pid 6UL) (at 4) guilds |> refused GuildError.GuildFull
        Expect.equal (GuildBook.decline (pid 5UL) (gid 1UL) (at 5) guilds |> ok).Player (pid 5UL) "declined"
        GuildBook.decline (pid 5UL) (gid 1UL) (at 5) guilds |> refused GuildError.TargetNotFound

    testCase "an invitation expires and the player limit is checked at acceptance too" <| fun _ ->
        let guilds = GuildBook.empty (limits 10 1 10 10)
        GuildBook.create (gid 1UL) (name "Стражи") (pid 1UL) (at 0) guilds |> ok |> ignore
        GuildBook.create (gid 2UL) (name "Вороны") (pid 2UL) (at 0) guilds |> ok |> ignore
        GuildBook.invite (pid 1UL) (gid 1UL) (pid 3UL) (at 0) guilds |> ok |> ignore
        GuildBook.invite (pid 2UL) (gid 2UL) (pid 3UL) (at 0) guilds |> ok |> ignore
        GuildBook.accept (pid 3UL) (gid 1UL) (at 1) guilds |> ok |> ignore
        GuildBook.accept (pid 3UL) (gid 2UL) (at 1) guilds |> refused GuildError.PlayerLimit
        GuildBook.invite (pid 1UL) (gid 1UL) (pid 4UL) (at 0) guilds |> ok |> ignore
        GuildBook.accept (pid 4UL) (gid 1UL) (at (7 * 24 * 60 + 1)) guilds |> refused GuildError.TargetNotFound
        let expired = GuildBook.expireInvites (at (7 * 24 * 60 + 1)) guilds
        Expect.equal (expired |> List.map _.Player |> List.sort) [ pid 3UL; pid 4UL ] "both pending invitations expired"
        Expect.isEmpty (GuildBook.invitesOf (pid 4UL) guilds) "nothing left"

    testCase "a lowered limit removes nobody and only refuses what would grow the excess" <| fun _ ->
        let guilds = staffed ()
        let lowered = GuildBook.withLimits (limits 10 3 2 4) guilds
        Expect.equal (GuildBook.tryFind (gid 1UL) lowered).Value.MemberCount 3 "three members stay over a limit of two"
        GuildBook.invite (pid 1UL) (gid 1UL) (pid 4UL) (at 3) lowered |> refused GuildError.GuildFull
        GuildBook.leave (pid 3UL) (gid 1UL) lowered |> ok |> ignore
        GuildBook.invite (pid 1UL) (gid 1UL) (pid 4UL) (at 3) lowered |> refused GuildError.GuildFull
        GuildBook.exclude (pid 1UL) (gid 1UL) (pid 2UL) lowered |> ok |> ignore
        GuildBook.invite (pid 1UL) (gid 1UL) (pid 4UL) (at 3) lowered |> ok |> ignore
        let restored =
            GuildBook.restore (limits 1 1 2 1)
                [ { Id = gid 1UL; Name = name "Стражи"; CreatedAt = at 0; Invites = []; Info = ValueNone
                    Titles = [ for id in 1UL .. 3UL -> { Id = tid id; Name = titleName $"Роль {id}" } ]
                    Members = [ for player in 1UL .. 5UL ->
                                    { Player = pid player; JoinedAt = at 0; Mute = ValueNone; Title = ValueSome(tid 1UL)
                                      Role = if player = 1UL then GuildRole.Master else GuildRole.Member } ] } ]
        let guild = (GuildBook.tryFind (gid 1UL) restored).Value
        Expect.equal guild.MemberCount 5 "stored guilds come back whole"
        Expect.equal guild.Titles.Length 3 "custom roles over a limit of two stay"

    testCase "discipline follows the role above the target; the master hands over and only then leaves" <| fun _ ->
        let guilds = staffed ()
        GuildBook.exclude (pid 2UL) (gid 1UL) (pid 1UL) guilds |> refused GuildError.NotPermitted
        GuildBook.mute (pid 3UL) (gid 1UL) (pid 2UL) SanctionTerm.UntilLifted reason (at 3) guilds |> refused GuildError.NotPermitted
        GuildBook.setRole (pid 2UL) (gid 1UL) (pid 3UL) GuildRole.Officer guilds |> refused GuildError.NotPermitted
        GuildBook.setRole (pid 1UL) (gid 1UL) (pid 3UL) GuildRole.Master guilds |> refused GuildError.NotPermitted
        GuildBook.leave (pid 1UL) (gid 1UL) guilds |> refused GuildError.MasterStays
        let _, previous, master = GuildBook.transfer (pid 1UL) (gid 1UL) (pid 3UL) guilds |> ok
        Expect.equal (previous |> ValueOption.map _.Role) (ValueSome GuildRole.Officer) "the old master becomes an officer"
        Expect.equal master.Role GuildRole.Master "the member is the master"
        Expect.equal (role guilds 1UL) (ValueSome GuildRole.Officer) "stored"
        GuildBook.leave (pid 1UL) (gid 1UL) guilds |> ok |> ignore
        // An administrator appoints a member; the master still in the guild becomes an officer.
        let _, replaced, appointed = GuildBook.appoint (gid 1UL) (pid 2UL) guilds |> ok
        Expect.equal (replaced |> ValueOption.map _.Player) (ValueSome(pid 3UL)) "the previous master"
        Expect.equal appointed.Player (pid 2UL) "the new master"
        GuildBook.appoint (gid 1UL) (pid 9UL) guilds |> refused GuildError.TargetNotFound

    testCase "a guild mute keeps reading only until lifted or expired" <| fun _ ->
        let guilds = staffed ()
        GuildBook.mute (pid 2UL) (gid 1UL) (pid 3UL) (SanctionTerm.For(TimeSpan.FromMinutes 10.0)) reason (at 3) guilds |> ok |> ignore
        GuildBook.mayWrite (pid 3UL) (gid 1UL) (at 4) guilds |> refused GuildError.NotPermitted
        GuildBook.mayWrite (pid 9UL) (gid 1UL) (at 4) guilds |> refused GuildError.NotFound
        GuildBook.unmute (pid 2UL) (gid 1UL) (pid 3UL) (at 5) guilds |> ok |> ignore
        GuildBook.mayWrite (pid 3UL) (gid 1UL) (at 5) guilds |> ok |> ignore
        GuildBook.unmute (pid 2UL) (gid 1UL) (pid 3UL) (at 5) guilds |> refused GuildError.TargetNotFound
        GuildBook.mute (pid 1UL) (gid 1UL) (pid 2UL) (SanctionTerm.For(TimeSpan.FromMinutes 10.0)) reason (at 5) guilds |> ok |> ignore
        Expect.isEmpty (GuildBook.expireMutes (at 14) guilds) "still in force"
        let ended = GuildBook.expireMutes (at 15) guilds
        Expect.equal (ended |> List.map (fun (_, membership) -> membership.Player)) [ pid 2UL ] "the officer's mute ended"
        GuildBook.mayWrite (pid 2UL) (gid 1UL) (at 15) guilds |> ok |> ignore

    testCase "a custom role's name follows the guild name rules" <| fun _ ->
        Expect.equal (GuildTitleName.value (titleName "  Казначей   гильдии ")) "Казначей гильдии" "trimmed, one space between words"
        Expect.isError (GuildTitleName.create 3 24 "Казначей!") "no punctuation"
        Expect.equal (GuildTitleName.create 3 24 "Ка") (Error(DomainError.InvalidText("GuildTitleName", TextError.TooShort 3))) "too short"
        Expect.equal (GuildTitleName.key (titleName "КАЗНАЧЕЙ")) (GuildTitleName.key (titleName "казначей")) "one name in any case"
        Expect.isError (GuildTitleId.create 0UL) "no zero ID"

    testCase "only the master creates, renames and deletes custom roles, unique in the guild and limited" <| fun _ ->
        let guilds = staffed ()
        GuildBook.createTitle (pid 2UL) (gid 1UL) (tid 1UL) (titleName "Казначей") guilds |> refused GuildError.NotPermitted
        GuildBook.createTitle (pid 9UL) (gid 1UL) (tid 1UL) (titleName "Казначей") guilds |> refused GuildError.NotFound
        let _, treasurer = GuildBook.createTitle (pid 1UL) (gid 1UL) (tid 1UL) (titleName "Казначей") guilds |> ok
        GuildBook.createTitle (pid 1UL) (gid 1UL) (tid 2UL) (titleName "КАЗНАЧЕЙ") guilds |> refused GuildError.TitleNameTaken
        GuildBook.createTitle (pid 1UL) (gid 1UL) (tid 2UL) (titleName "Новобранец") guilds |> ok |> ignore
        GuildBook.createTitle (pid 1UL) (gid 1UL) (tid 3UL) (titleName "Летописец") guilds |> refused GuildError.TitlesFull
        GuildBook.renameTitle (pid 1UL) (gid 1UL) treasurer.Id (titleName "новобранец") guilds |> refused GuildError.TitleNameTaken
        GuildBook.renameTitle (pid 2UL) (gid 1UL) treasurer.Id (titleName "Хранитель") guilds |> refused GuildError.NotPermitted
        GuildBook.renameTitle (pid 1UL) (gid 1UL) (tid 7UL) (titleName "Хранитель") guilds |> refused GuildError.TitleNotFound
        let _, renamed = GuildBook.renameTitle (pid 1UL) (gid 1UL) treasurer.Id (titleName "КАЗНАЧЕЙ") guilds |> ok
        Expect.equal (GuildTitleName.value renamed.Name) "КАЗНАЧЕЙ" "the role itself may change case"
        let guild = (GuildBook.tryFind (gid 1UL) guilds).Value
        Expect.equal (guild.Titles |> List.map _.Id) [ tid 1UL; tid 2UL ] "oldest first"
        GuildBook.deleteTitle (pid 2UL) (gid 1UL) (tid 2UL) guilds |> refused GuildError.NotPermitted
        GuildBook.deleteTitle (pid 1UL) (gid 1UL) (tid 2UL) guilds |> ok |> ignore
        GuildBook.createTitle (pid 1UL) (gid 1UL) (tid 3UL) (titleName "Летописец") guilds |> ok |> ignore

    testCase "the master gives a member one custom role, which a deletion takes away" <| fun _ ->
        let guilds = staffed ()
        GuildBook.createTitle (pid 1UL) (gid 1UL) (tid 1UL) (titleName "Казначей") guilds |> ok |> ignore
        GuildBook.createTitle (pid 1UL) (gid 1UL) (tid 2UL) (titleName "Новобранец") guilds |> ok |> ignore
        GuildBook.setTitle (pid 2UL) (gid 1UL) (pid 3UL) (ValueSome(tid 1UL)) guilds |> refused GuildError.NotPermitted
        GuildBook.setTitle (pid 1UL) (gid 1UL) (pid 3UL) (ValueSome(tid 9UL)) guilds |> refused GuildError.TitleNotFound
        GuildBook.setTitle (pid 1UL) (gid 1UL) (pid 9UL) (ValueSome(tid 1UL)) guilds |> refused GuildError.TargetNotFound
        GuildBook.setTitle (pid 1UL) (gid 1UL) (pid 3UL) (ValueSome(tid 1UL)) guilds |> ok |> ignore
        GuildBook.setTitle (pid 1UL) (gid 1UL) (pid 1UL) (ValueSome(tid 1UL)) guilds |> ok |> ignore
        GuildBook.setTitle (pid 1UL) (gid 1UL) (pid 3UL) (ValueSome(tid 2UL)) guilds |> ok |> ignore
        Expect.equal (titleOf guilds 3UL) (ValueSome(tid 2UL)) "one role: the new one replaces the old"
        GuildBook.setRole (pid 1UL) (gid 1UL) (pid 3UL) GuildRole.Officer guilds |> ok |> ignore
        Expect.equal (titleOf guilds 3UL) (ValueSome(tid 2UL)) "a rank change keeps the custom role"
        let _, cleared = GuildBook.deleteTitle (pid 1UL) (gid 1UL) (tid 1UL) guilds |> ok
        Expect.equal (cleared |> List.map _.Player) [ pid 1UL ] "the holder of the deleted role"
        Expect.equal (titleOf guilds 1UL) ValueNone "lost it"
        GuildBook.setTitle (pid 1UL) (gid 1UL) (pid 3UL) ValueNone guilds |> ok |> ignore
        Expect.equal (titleOf guilds 3UL) ValueNone "taken away"

    testCase "guild information keeps the chat text rules and only the master writes or clears it" <| fun _ ->
        let info = GuildInfo.create 100 "Собираемся по пятницам.\nНовичкам рады!" |> ok
        Expect.equal (GuildInfo.value info) "Собираемся по пятницам.\nНовичкам рады!" "kept as written, line breaks included"
        Expect.isError (GuildInfo.create 100 "   ") "blank"
        Expect.isError (GuildInfo.create 100 "звонок\u0007") "a control character"
        Expect.equal (GuildInfo.create 5 "шесть!") (Error(DomainError.InvalidText("GuildInfo", TextError.TooLong 5))) "too long"
        let guilds = staffed ()
        GuildBook.setInfo (pid 2UL) (gid 1UL) (ValueSome info) guilds |> refused GuildError.NotPermitted
        GuildBook.setInfo (pid 9UL) (gid 1UL) (ValueSome info) guilds |> refused GuildError.NotFound
        Expect.equal (GuildBook.setInfo (pid 1UL) (gid 1UL) (ValueSome info) guilds |> ok).Info (ValueSome info) "written"
        Expect.equal (GuildBook.setInfo (pid 1UL) (gid 1UL) ValueNone guilds |> ok).Info ValueNone "cleared"

    testCase "the master removes any message, an officer a member's, a member none" <| fun _ ->
        let guilds = staffed ()
        let may actor author = GuildBook.mayRemoveMessage (pid actor) (gid 1UL) author guilds |> Result.isOk
        Expect.isTrue (may 1UL (ValueSome(pid 2UL))) "master over an officer"
        Expect.isTrue (may 1UL (ValueSome(pid 1UL))) "master over their own message"
        Expect.isTrue (may 2UL (ValueSome(pid 3UL))) "officer over a member"
        Expect.isFalse (may 2UL (ValueSome(pid 1UL))) "officer not over the master"
        Expect.isFalse (may 2UL (ValueSome(pid 2UL))) "authors do not remove their own messages"
        Expect.isTrue (may 2UL (ValueSome(pid 8UL))) "an author who left ranks as a member"
        Expect.isFalse (may 3UL (ValueSome(pid 3UL))) "a member removes nothing"
        GuildBook.mayRemoveMessage (pid 9UL) (gid 1UL) ValueNone guilds |> refused GuildError.NotFound
]
