module Dreamsleeve.Server.Tests.PlayerDetailsTests

open System
open Expecto
open Dreamsleeve.Server.Domain

let private ok = function Ok value -> value | Error error -> failtestf "Unexpected error: %A" error

let tests = testList "Player details" [
    testCase "activities preserve target labels and validate their shape" <| fun _ ->
        let combat = PlayerActivity.create 256 64 ActivityKind.Combat (ValueSome "Mudcrab") LockDifficulty.Unknown ValueNone |> ok
        Expect.equal combat.TargetName (ValueSome "Mudcrab") "Target remains presentation data."
        Expect.isError (PlayerActivity.create 256 64 ActivityKind.Swimming (ValueSome "Mudcrab") LockDifficulty.Unknown ValueNone) "Swimming has no target."
        Expect.isError (PlayerActivity.create 256 64 ActivityKind.Combat ValueNone LockDifficulty.Hard ValueNone) "Lock difficulty belongs to lockpicking."
        let picking = PlayerActivity.create 256 64 ActivityKind.Lockpicking ValueNone LockDifficulty.VeryEasy ValueNone |> ok
        Expect.equal picking.LockDifficulty LockDifficulty.VeryEasy "An unnamed lock can still have difficulty."

    testCase "menu identity is canonical while labels are not rewritten" <| fun _ ->
        let menu = PlayerActivity.create 256 64 ActivityKind.Menu ValueNone LockDifficulty.Unknown (ValueSome "Skyrim:Inventory") |> ok
        Expect.equal menu.MenuKey (ValueSome "skyrim:inventory") "Stable key, no localized menu sentence."
        Expect.isError (PlayerActivity.create 256 64 ActivityKind.Menu ValueNone LockDifficulty.Unknown ValueNone) "Menu must identify itself."
        Expect.isError (PlayerActivity.create 256 64 ActivityKind.Exploring ValueNone LockDifficulty.Unknown (ValueSome "map")) "Non-menu activities cannot carry a menu."
        Expect.isError (PlayerActivity.create 256 64 ActivityKind.Menu ValueNone LockDifficulty.Unknown (ValueSome "bad key")) "Keys do not contain spaces."
        let label = " e\u0301 "
        let talking = PlayerActivity.create 256 64 ActivityKind.Talking (ValueSome label) LockDifficulty.Unknown ValueNone |> ok
        Expect.equal talking.TargetName (ValueSome label) "Game labels preserve spelling and whitespace."

    testCase "details distinguish unknown from an invalid zero level" <| fun _ ->
        let details = PlayerDetails.create ValueNone (ValueSome UInt32.MaxValue) PlayerActivity.unknown ValueNone ValueNone |> ok
        Expect.equal details.Level (ValueSome UInt32.MaxValue) "No arbitrary gameplay level cap."
        Expect.equal PlayerDetails.empty.Level ValueNone "An unreported level is unknown."
        Expect.isError (PlayerDetails.create ValueNone (ValueSome 0u) PlayerActivity.unknown ValueNone ValueNone) "Zero is not a reported level."

    testCase "place labels and race keys remain separate from coordinate identity" <| fun _ ->
        let form = FormKey.create (PluginName.create 260 "Skyrim.esm" |> ok) (LocalFormId.create 0x13746u |> ok)
        let race = NamedForm.create 256 form "Nord" |> ok
        let place = PlaceDescription.create 256 64 "Skyrim" "Whiterun Hold" "Western Watchtower" "IMPERIAL_TOWER" false |> ok
        let start = DateTimeOffset.FromUnixTimeMilliseconds 1700000000000L
        let details = PlayerDetails.create (ValueSome race) (ValueSome 2u) PlayerActivity.unknown (ValueSome place) (ValueSome start) |> ok
        Expect.equal race.Form form "Race is a plugin-relative form."
        Expect.equal place.MarkerKind "imperial_tower" "Marker kind is an extensible canonical key."
        Expect.equal details.GameStartedAt (ValueSome start) "Client-reported start time is kept, not replaced by connection time."

    testCase "text limits and invalid unicode are rejected before ownership" <| fun _ ->
        Expect.isError (PlayerActivity.create 3 64 ActivityKind.Combat (ValueSome "long") LockDifficulty.Unknown ValueNone) "Target label bound."
        Expect.isError (PlayerActivity.create 256 64 ActivityKind.Combat (ValueSome "bad\n") LockDifficulty.Unknown ValueNone) "Control characters rejected before any trimming."
        Expect.isError (PlaceDescription.create 256 64 "" "" "" "bad key" false) "Marker key format."
        Expect.isError (PlaceDescription.create 256 64 "" "" (string (char 0xD800)) "" false) "Malformed UTF-16 rejected."
]
