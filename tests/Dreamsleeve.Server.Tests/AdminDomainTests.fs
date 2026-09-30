module Dreamsleeve.Server.Tests.AdminDomainTests

open System
open Dreamsleeve.Server.Domain
open Expecto
open AgentTests

let private ok = function Ok value -> value | Error error -> failwithf "%A" error
let private profile id username display =
    PlayerData.create (PlayerId.create id |> ok) (Username.create 32 username |> ok) (DisplayName.create 64 display |> ok)
let private now = DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero)

let tests = testList "Admin domain" [
    testCase "roles keep stable numbers and keys and belong only to registered players" <| fun _ ->
        equal [ 0; 1 ] (PlayerRole.all |> List.map PlayerRole.toInt)
        for role in PlayerRole.all do
            equal (ValueSome role) (PlayerRole.ofInt (PlayerRole.toInt role))
            equal (Some role) (PlayerRole.ofKey (PlayerRole.key role))
        equal ValueNone (PlayerRole.ofInt 2)
        equal None (PlayerRole.ofKey "admin")
        let player = profile 7UL "player" "Player"
        equal (Ok { PlayerId = player.PlayerId; Role = PlayerRole.Moderator }) (PlayerRole.assign (ValueSome player) PlayerRole.Moderator)
        equal (Error AdminError.PlayerNotFound) (PlayerRole.assign ValueNone PlayerRole.Moderator)

    testCase "audit actions have distinct stored keys that read back" <| fun _ ->
        let keys = AdminAction.all |> List.map AdminAction.key
        equal 9 (List.length keys)
        equal (List.length keys) (keys |> List.distinct |> List.length)
        for action in AdminAction.all do equal (Some action) (AdminAction.ofKey (AdminAction.key action))
        equal "player:42" (AuditTarget.key (AuditTarget.Player(PlayerId.create 42UL |> ok)))
        equal "admin:3" (AuditTarget.key (AuditTarget.Admin(AdminId.create 3L |> ok)))
        let long = AuditRecord.create AdminAction.Announced AuditTarget.Server (String('x', 2000))
        equal AuditRecord.MaxDetails long.Details.Length
        equal "" (AuditRecord.create AdminAction.RevokePlayerAccess AuditTarget.Server null).Details
        check (AdminId.create 0L |> Result.isError) "IDs are positive."

    testCase "a one-time code opens its purpose once, expires and is replaced by the next one" <| fun _ ->
        let lifetime = TimeSpan.FromMinutes 15.
        let setup = function AdminCodePurpose.Setup -> true | AdminCodePurpose.ResetPassword _ -> false
        let codes = AdminCodes.issue AdminCodePurpose.Setup "hash-1" now lifetime AdminCodes.empty
        // A reset page does not accept the setup code; the attempt does not spend it.
        let refused, codes = AdminCodes.redeem (setup >> not) "hash-1" now codes
        equal (Error AdminError.CodeInvalid) refused
        let used, codes = AdminCodes.redeem setup "hash-1" now codes
        equal (Ok AdminCodePurpose.Setup) used
        let again, _ = AdminCodes.redeem setup "hash-1" now codes
        equal (Error AdminError.CodeInvalid) again
        // Issuing again replaces the previous code of the same purpose.
        let codes = AdminCodes.issue AdminCodePurpose.Setup "hash-2" now lifetime AdminCodes.empty
        let codes = AdminCodes.issue AdminCodePurpose.Setup "hash-3" now lifetime codes
        equal 1 (AdminCodes.count codes)
        equal (Error AdminError.CodeInvalid) (fst (AdminCodes.redeem setup "hash-2" now codes))
        // Expiry spends the code as well.
        let late, remaining = AdminCodes.redeem setup "hash-3" (now + lifetime) codes
        equal (Error AdminError.CodeExpired) late
        equal 0 (AdminCodes.count remaining)
        // Reset codes are kept per administrator.
        let first, second = AdminId.create 1L |> ok, AdminId.create 2L |> ok
        let resets =
            AdminCodes.empty
            |> AdminCodes.issue (AdminCodePurpose.ResetPassword first) "a" now lifetime
            |> AdminCodes.issue (AdminCodePurpose.ResetPassword second) "b" now lifetime
        equal 2 (AdminCodes.count resets)
        equal (Ok (AdminCodePurpose.ResetPassword second)) (fst (AdminCodes.redeem (setup >> not) "b" now resets))

    testCase "panel sessions end at their deadline and on a new password of their administrator" <| fun _ ->
        let admin = AdminId.create 5L |> ok
        let session = PanelSession.create "hash" admin now (TimeSpan.FromHours 12.)
        check (PanelSession.isActive (now + TimeSpan.FromHours 11.9) session) "active before the deadline"
        check (not (PanelSession.isActive (now + TimeSpan.FromHours 12.) session)) "ended at the deadline"
        check (PanelSession.revokedBy admin session) "a new password ends it"
        check (not (PanelSession.revokedBy (AdminId.create 6L |> ok) session)) "other administrators do not"

    testCase "token labels and panel announcements follow the text rules" <| fun _ ->
        equal "CI bot" (ApiTokenLabel.create "  CI bot " |> ok |> ApiTokenLabel.value)
        for invalid in [ ""; "   "; String('x', 65); "line\nbreak"; null ] do
            check (ApiTokenLabel.create invalid |> Result.isError) $"label refused: {invalid}"
        let text, kind = AdminAnnouncement.create 2000 "Restart in 5 minutes" "event" |> ok
        equal "Restart in 5 minutes" (ChatMessageText.value text)
        equal AnnouncementKind.Event kind
        check (AdminAnnouncement.create 2000 "Text" "periodic" |> Result.isError) "the schedule's kind is not manual"
        check (AdminAnnouncement.create 5 "Too long text" "admin" |> Result.isError) "chat length limit"
        check (AdminAnnouncement.create 2000 "   " "admin" |> Result.isError) "blank text"

    testCase "the panel view carries the real identity of a hidden player next to the pseudonym" <| fun _ ->
        let stored = profile 9UL "alice.real" "Алиса Настоящая"
        let player =
            Player.create stored
            |> Player.beginCharacter (CharacterName.create 64 "Секретная Героиня" |> ok)
        let pseudonym = Pseudonym.create "Страж" |> ok
        let hidden = AdminPlayerView.create stored player false (ValueSome pseudonym) HiddenIdentity.Everywhere PlayerRole.Moderator AdminSessionPhase.Active now
        equal "alice.real" (Username.value hidden.Username)
        equal "Алиса Настоящая" (DisplayName.value hidden.DisplayName)
        equal (ValueSome "Секретная Героиня") (hidden.CharacterName |> ValueOption.map CharacterName.value)
        equal (ValueSome "Страж") (hidden.Pseudonym |> ValueOption.map Pseudonym.value)
        equal HiddenIdentity.Everywhere hidden.Hiding
        equal PlayerRole.Moderator hidden.Role
        let shown = AdminPlayerView.create stored player true ValueNone HiddenIdentity.Everywhere PlayerRole.Player AdminSessionPhase.Opening now
        equal HiddenIdentity.Shown shown.Hiding
        equal ValueNone shown.Pseudonym
        check shown.CharacterWithheld "a withheld character name is still shown to the panel, marked"
        equal (ValueSome "Секретная Героиня") (shown.CharacterName |> ValueOption.map CharacterName.value)
]
