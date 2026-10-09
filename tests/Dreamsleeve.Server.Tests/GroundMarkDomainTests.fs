module Dreamsleeve.Server.Tests.GroundMarkDomainTests

open System
open Expecto
open Dreamsleeve.Server.Domain

let private ok = function
    | Ok value -> value
    | Error error -> failtestf "Expected success, received %A" error

let private playerId value = PlayerId.create value |> ok
let private markId value = GroundMarkId.create value |> ok
let private formKey plugin id = FormKey.create (PluginName.create 255 plugin |> ok) (LocalFormId.create id |> ok)
let private whiterun = formKey "Skyrim.esm" 0x1A26Fu
let private riften = formKey "Skyrim.esm" 0x16BB4u
let private position x = Position.create x 0.0f 0.0f |> ok
let private heading = Radian.create 1.5f |> ok
let private placement space x = GroundMarkPlacement.create space (position x) heading
let private observer space x =
    ValueSome (PlayerLocation.create (Location.create space (LocationName.create 128 "" |> ok)) (position x) CameraDirection.zero)
let private rules = GroundMarkRules.create 2 3 30 7 100.0f 50.0f |> ok
let private epoch = DateTimeOffset.UnixEpoch
let private note id author x text =
    GroundMark.create (markId id) (playerId author) (GroundMarkBody.Note (GroundNoteText.create 200 text |> ok)) (placement whiterun x) epoch
let private death id author x label =
    GroundMark.create (markId id) (playerId author) (GroundMarkBody.Death (DeathMarkText.create 64 label |> ok)) (placement whiterun x) epoch
let private ids (marks: GroundMark list) = marks |> List.map (fun mark -> GroundMarkId.value mark.Id)

let tests = testList "Dreamsleeve.Server.Domain.GroundMarks" [
    testCase "note text follows the chat pipeline and death labels are one line that may be empty" <| fun _ ->
        let note = GroundNoteText.create 200 "  praise\n\tthe sun  " |> ok
        Expect.equal (GroundNoteText.value note) "  praise\n\tthe sun  " "Original note text is kept."
        Expect.isError (GroundNoteText.create 200 "   ") "A blank note is refused."
        Expect.isError (GroundNoteText.create 200 "a\u0000b") "NUL is not note text."
        Expect.equal (GroundNoteText.create 3 "abcd") (Error (DomainError.InvalidText ("GroundNoteText", TextError.TooLong 3))) "Length in scalars."
        Expect.equal (DeathMarkText.create 64 "" |> ok |> DeathMarkText.value) "" "A death without a killer has no label."
        Expect.equal (DeathMarkText.create 64 " Драугр-повелитель " |> ok |> DeathMarkText.value) " Драугр-повелитель " "Label kept as sent."
        for invalid in [ "two\nlines"; "tab\there"; "bell\u0007"; null; String [| char 0xD800 |] ] do
            Expect.isError (DeathMarkText.create 64 invalid) (sprintf "Refused label %A" invalid)
        Expect.isError (DeathMarkText.create 2 "abc") "Label limit applies."
        Expect.isError (GroundMarkId.create 0UL) "Zero is not a mark ID."

    testCase "rules require positive quotas, treat zero days as no expiry and finite distances" <| fun _ ->
        Expect.isError (GroundMarkRules.create 0 1 0 0 1.0f 0.0f) "A note quota below one is invalid."
        Expect.isError (GroundMarkRules.create 1 0 0 0 1.0f 0.0f) "A death quota below one is invalid."
        Expect.isError (GroundMarkRules.create 1 1 -1 0 1.0f 0.0f) "Negative lifetime."
        Expect.isError (GroundMarkRules.create 1 1 0 0 Single.NaN 0.0f) "Radius must be finite."
        Expect.isError (GroundMarkRules.create 1 1 0 0 1.0f -1.0f) "Distance must not be negative."
        let forever = GroundMarkRules.create 1 1 0 0 0.0f 0.0f |> ok
        Expect.equal (GroundMarkRules.ttl forever GroundMarkKind.Note) ValueNone "Zero days never expires."
        Expect.equal (GroundMarkRules.ttl rules GroundMarkKind.Death) (ValueSome (TimeSpan.FromDays 7.0)) "Death marks live seven days."
        Expect.equal (GroundMarkRules.quota rules GroundMarkKind.Note) 2 "Quota per kind."

    testCase "a mark expires exactly at the end of its lifetime and only when the kind has one" <| fun _ ->
        let mark = note 1UL 1UL 0.0f "hello"
        Expect.isFalse (GroundMark.isExpired rules (epoch + TimeSpan.FromDays 30.0 - TimeSpan.FromMilliseconds 1.0) mark) "Alive before the deadline."
        Expect.isTrue (GroundMark.isExpired rules (epoch + TimeSpan.FromDays 30.0) mark) "Expiry is inclusive."
        let forever = GroundMarkRules.create 1 1 0 0 0.0f 0.0f |> ok
        Expect.isFalse (GroundMark.isExpired forever DateTimeOffset.MaxValue mark) "No lifetime, no expiry."
        Expect.equal (GroundMark.expiresAt rules (death 2UL 1UL 0.0f "")) (ValueSome (epoch + TimeSpan.FromDays 7.0)) "Death deadline follows its kind."
        Expect.equal mark.Kind GroundMarkKind.Note "Kind follows the body."
        Expect.equal mark.Text "hello" "Text follows the body."

    testCase "visibility needs the same space, an observer position and the radius boundary included" <| fun _ ->
        let radius = rules.VisibilityDistance
        let mark = note 1UL 1UL 0.0f "hello"
        Expect.isTrue (GroundMark.isVisibleFrom radius (observer whiterun 100.0f) mark) "Boundary is inside."
        Expect.isFalse (GroundMark.isVisibleFrom radius (observer whiterun 100.001f) mark) "Just outside."
        Expect.isFalse (GroundMark.isVisibleFrom radius (observer riften 0.0f) mark) "Another space never sees it."
        Expect.isFalse (GroundMark.isVisibleFrom radius ValueNone mark) "No position, nothing on the ground."
        Expect.isTrue (GroundMark.isVisibleFrom 0.0f<worldUnit> (observer whiterun 0.0f) mark) "Zero radius keeps the exact point."

    testCase "placement check is soft: unknown author position passes, another space fails, zero limit disables distance" <| fun _ ->
        let target = placement whiterun 0.0f
        Expect.isTrue (GroundMarkPlacement.isNear rules.MaxPlacementDistance ValueNone target) "Unknown last position passes."
        Expect.isTrue (GroundMarkPlacement.isNear rules.MaxPlacementDistance (observer whiterun 50.0f) target) "Within the limit."
        Expect.isFalse (GroundMarkPlacement.isNear rules.MaxPlacementDistance (observer whiterun 50.5f) target) "Beyond the limit."
        Expect.isFalse (GroundMarkPlacement.isNear rules.MaxPlacementDistance (observer riften 0.0f) target) "Another space fails even without a distance limit."
        Expect.isFalse (GroundMarkPlacement.isNear 0.0f<worldUnit> (observer riften 0.0f) target) "Space rule stays when distance is disabled."
        Expect.isTrue (GroundMarkPlacement.isNear 0.0f<worldUnit> (observer whiterun 1.0e6f) target) "Zero disables the distance rule."

    testCase "quota evicts the oldest mark of the same author and kind only" <| fun _ ->
        let storage = GroundMarkStorage.create ()
        Expect.equal (GroundMarkStorage.add rules (note 1UL 1UL 0.0f "one") storage |> ok) ValueNone "Room for the first."
        Expect.equal (GroundMarkStorage.add rules (note 2UL 1UL 0.0f "two") storage |> ok) ValueNone "Room for the second."
        Expect.equal (GroundMarkStorage.add rules (note 3UL 2UL 0.0f "other author") storage |> ok) ValueNone "Another author has an own quota."
        Expect.equal (GroundMarkStorage.add rules (death 4UL 1UL 0.0f "wolf") storage |> ok) ValueNone "Another kind has an own quota."
        let candidate = GroundMarkStorage.evictionCandidate rules (playerId 1UL) GroundMarkKind.Note storage
        Expect.equal (candidate |> ValueOption.map (fun mark -> GroundMarkId.value mark.Id)) (ValueSome 1UL) "The oldest note would go."
        let evicted = GroundMarkStorage.add rules (note 5UL 1UL 0.0f "three") storage |> ok
        Expect.equal (evicted |> ValueOption.map (fun mark -> GroundMarkId.value mark.Id)) (ValueSome 1UL) "The oldest note went."
        Expect.equal (GroundMarkStorage.snapshot storage |> ids) [2UL; 3UL; 4UL; 5UL] "Others are untouched."
        Expect.equal (GroundMarkStorage.countOf (playerId 1UL) GroundMarkKind.Note storage) 2 "Quota holds."
        Expect.equal (GroundMarkStorage.add rules (note 5UL 1UL 0.0f "dup") storage) (Error (DomainError.DuplicateGroundMark (markId 5UL))) "IDs are unique."
        Expect.equal (GroundMarkStorage.count storage) 4 "A refused add changes nothing."

    testCase "a lowered quota evicts one mark per placement and never destroys existing ones" <| fun _ ->
        let storage = GroundMarkStorage.create ()
        let generous = GroundMarkRules.create 5 5 0 0 0.0f 0.0f |> ok
        for id in 1UL .. 4UL do GroundMarkStorage.add generous (note id 1UL 0.0f "n") storage |> ok |> ignore
        let evicted = GroundMarkStorage.add rules (note 9UL 1UL 0.0f "new") storage |> ok
        Expect.equal (evicted |> ValueOption.map (fun mark -> GroundMarkId.value mark.Id)) (ValueSome 1UL) "Only the oldest gives way."
        Expect.equal (GroundMarkStorage.countOf (playerId 1UL) GroundMarkKind.Note storage) 4 "The surplus is not purged."

    testCase "removal and expiry keep the per-author index consistent" <| fun _ ->
        let storage = GroundMarkStorage.create ()
        let old = GroundMark.create (markId 1UL) (playerId 1UL) (GroundMarkBody.Death (DeathMarkText.create 64 "fall" |> ok)) (placement whiterun 0.0f) epoch
        let fresh = GroundMark.create (markId 2UL) (playerId 1UL) (GroundMarkBody.Note (GroundNoteText.create 200 "n" |> ok)) (placement whiterun 0.0f) (epoch + TimeSpan.FromDays 29.0)
        GroundMarkStorage.add rules old storage |> ok |> ignore
        GroundMarkStorage.add rules fresh storage |> ok |> ignore
        Expect.equal (GroundMarkStorage.expired rules (epoch + TimeSpan.FromDays 7.0) storage |> ids) [1UL] "Only the death mark is past its lifetime."
        Expect.equal (GroundMarkStorage.remove (markId 1UL) storage |> ValueOption.map (fun mark -> mark.Text)) (ValueSome "fall") "Removal returns the mark."
        Expect.equal (GroundMarkStorage.remove (markId 1UL) storage) ValueNone "Second removal finds nothing."
        Expect.isTrue (GroundMarkStorage.authorHasMarks (playerId 1UL) storage) "The note remains."
        GroundMarkStorage.remove (markId 2UL) storage |> ignore
        Expect.isFalse (GroundMarkStorage.authorHasMarks (playerId 1UL) storage) "Nothing remains of the author."
        Expect.equal (GroundMarkStorage.evictionCandidate rules (playerId 1UL) GroundMarkKind.Note storage) ValueNone "No candidate without marks."
]
