module Dreamsleeve.Server.Tests.PlayerDetailsTests

open System
open Expecto
open Dreamsleeve.Server.Domain

let private ok = function Ok value -> value | Error error -> failtestf "Unexpected error: %A" error

let private race = NamedForm.create 256 (FormKey.create (PluginName.create 260 "Skyrim.esm" |> ok) (LocalFormId.create 0x13746u |> ok)) "Nord" |> ok
let private place = PlaceDescription.create 256 64 "Skyrim" "Whiterun Hold" "Western Watchtower" "imperial_tower" false |> ok
let private start = DateTimeOffset.FromUnixTimeMilliseconds 1700000000000L
let private full = PlayerDetails.create (ValueSome race) (ValueSome 10u) PlayerActivity.unknown (ValueSome place) (ValueSome start)
let private unchanged = { Race = ValueNone; Level = ValueNone; Activity = ValueNone; Place = ValueNone; GameStartedAt = ValueNone }

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

    testCase "details preserve the full uint32 level range and distinguish unknown" <| fun _ ->
        let details = PlayerDetails.create ValueNone (ValueSome UInt32.MaxValue) PlayerActivity.unknown ValueNone ValueNone
        Expect.equal details.Level (ValueSome UInt32.MaxValue) "No arbitrary gameplay level cap."
        Expect.equal PlayerDetails.empty.Level ValueNone "An unreported level is unknown."
        let zero = PlayerDetails.create ValueNone (ValueSome 0u) PlayerActivity.unknown ValueNone ValueNone
        Expect.equal zero.Level (ValueSome 0u) "A reported zero level is not unknown."

    testCase "place labels and race keys remain separate from coordinate identity" <| fun _ ->
        let form = FormKey.create (PluginName.create 260 "Skyrim.esm" |> ok) (LocalFormId.create 0x13746u |> ok)
        let race = NamedForm.create 256 form "Nord" |> ok
        let place = PlaceDescription.create 256 64 "Skyrim" "Whiterun Hold" "Western Watchtower" "IMPERIAL_TOWER" false |> ok
        let start = DateTimeOffset.FromUnixTimeMilliseconds 1700000000000L
        let details = PlayerDetails.create (ValueSome race) (ValueSome 2u) PlayerActivity.unknown (ValueSome place) (ValueSome start)
        Expect.equal race.Form form "Race is a plugin-relative form."
        Expect.equal place.MarkerKind "imperial_tower" "Marker kind is an extensible canonical key."
        Expect.equal details.GameStartedAt (ValueSome start) "Client-reported start time is kept, not replaced by connection time."

    testCase "text limits and invalid unicode are rejected before ownership" <| fun _ ->
        Expect.isError (PlayerActivity.create 3 64 ActivityKind.Combat (ValueSome "long") LockDifficulty.Unknown ValueNone) "Target label bound."
        Expect.isError (PlayerActivity.create 256 64 ActivityKind.Combat (ValueSome "bad\n") LockDifficulty.Unknown ValueNone) "Control characters rejected before any trimming."
        Expect.isError (PlaceDescription.create 256 64 "" "" "" "bad key" false) "Marker key format."
        Expect.isError (PlaceDescription.create 256 64 "" "" (string (char 0xD800)) "" false) "Malformed UTF-16 rejected."

    testCase "a details patch carries only the components that changed" <| fun _ ->
        let combat = PlayerActivity.create 256 64 ActivityKind.Combat (ValueSome "Mudcrab") LockDifficulty.Unknown ValueNone |> ok
        let fighting = PlayerDetails.create (ValueSome race) (ValueSome 10u) combat (ValueSome place) (ValueSome start)
        Expect.equal (DetailsPatch.between full fighting) (ValueSome { unchanged with Activity = ValueSome combat }) "Activity alone."
        let levelled = PlayerDetails.create (ValueSome race) (ValueSome 11u) PlayerActivity.unknown (ValueSome place) (ValueSome start)
        Expect.equal (DetailsPatch.between full levelled) (ValueSome { unchanged with Level = ValueSome (ValueSome 11u) }) "A new level replaces the old one."

    testCase "optional components that became unknown are cleared" <| fun _ ->
        Expect.equal (DetailsPatch.between full PlayerDetails.empty)
            (ValueSome { Race = ValueSome ValueNone; Level = ValueSome ValueNone; Activity = ValueNone
                         Place = ValueSome ValueNone; GameStartedAt = ValueSome ValueNone })
            "Every optional component clears; the unchanged activity is left out."
        Expect.equal (DetailsPatch.between PlayerDetails.empty full)
            (ValueSome { unchanged with Race = ValueSome (ValueSome race); Level = ValueSome (ValueSome 10u)
                                        Place = ValueSome (ValueSome place); GameStartedAt = ValueSome (ValueSome start) })
            "Components that became known are set."

    testCase "equal details make no patch" <| fun _ ->
        let copy = PlayerDetails.create (ValueSome race) (ValueSome 10u) PlayerActivity.unknown (ValueSome place) (ValueSome start)
        Expect.equal (DetailsPatch.between full copy) ValueNone "Same components."
        Expect.equal (DetailsPatch.between PlayerDetails.empty PlayerDetails.empty) ValueNone "Nothing known on either side."
]
