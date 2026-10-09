module Dreamsleeve.Server.Tests.DomainTests

open System
open System.Globalization
open Expecto
open Faqt
open Dreamsleeve.Server.Domain

let private ok = function
    | Ok value -> value
    | Error error -> failtestf "Expected success, received %A" error

let private playerId value = PlayerId.create value |> ok
let private channelId value = ChatChannelId.create value |> ok
let private messageId value = ChatMessageId.create value |> ok
let private displayName value = DisplayName.create 64 value |> ok
let private characterName value = CharacterName.create 128 value |> ok
let private actorKey value = ActorValueKey.create 128 value |> ok

let private profile id name =
    PlayerData.create (playerId id) (Username.create 32 (sprintf "user%d" id) |> ok) (displayName name) NameColor.unknown

let private formKey plugin id =
    FormKey.create (PluginName.create 255 plugin |> ok) (LocalFormId.create id |> ok)

let private location key name position =
    PlayerLocation.create (Location.create key (LocationName.create 128 name |> ok)) position CameraDirection.zero

let private label value = ActorValueName.create 64 value |> ok
let private reading name amount = ActorValueInfo.create (label name) (ActorValueState.resource amount 100)
let private health amount = reading "Health" amount

let private chat capacity =
    let value = Chat.create ChatChannels.globalId ChatChannelKind.Global capacity |> ok
    Chat.join (playerId 1UL) value |> ignore
    value

let private message channel id author =
    ChatMessage.create (messageId id) (channelId channel) (PublicIdentity.Profile author) ValueNone
        (ChatMessageText.create 2000 "Hello" |> ok) DateTimeOffset.UnixEpoch

let private append id value =
    Chat.append (message 1UL id (profile 1UL "First")) value |> ok

let private historyIds (page: ChatHistoryPage) =
    page.Messages |> List.map (fun (message: ChatMessage) -> ChatMessageId.value message.MessageId)

let private textTests =
    testList "text and identifiers" [
        testCase "username is ASCII and culture independent" <| fun _ ->
            let previous = CultureInfo.CurrentCulture
            try
                CultureInfo.CurrentCulture <- CultureInfo.GetCultureInfo "tr-TR"
                let actual = Username.create 32 "  NEWRI_I.01  " |> ok |> Username.value
                actual.Should().Be("newri_i.01") |> ignore
            finally
                CultureInfo.CurrentCulture <- previous

        testCase "username rejects non-ASCII instead of folding it into ASCII" <| fun _ ->
            for input in [ "new rite"; "@newrite"; "Игрок"; "\u212Aelvin"; "newrite\n" ] do
                Expect.isError (Username.create 32 input) (sprintf "Reject %A" input)

        testCase "display name uses NFC before measuring its length" <| fun _ ->
            let actual = DisplayName.create 1 "  E\u0301  " |> ok |> DisplayName.value
            actual.Should().Be("É") |> ignore

        testCase "display name limit counts Unicode scalars, not UTF-16 code units" <| fun _ ->
            DisplayName.create 1 "\U0001F600" |> ok |> DisplayName.value
            |> fun value -> value.Should().Be("\U0001F600") |> ignore
            Expect.isError (DisplayName.create 1 "\U0001F600a") "Two scalars exceed a one-scalar limit"

        testCase "a name color is any 24-bit RGB value; a player may choose only a readable one" <| fun _ ->
            Expect.equal (NameColor.create 0xFFFFFFu |> ok |> NameColor.value) 0xFFFFFFu "white"
            Expect.isError (NameColor.create 0x1000000u) "beyond 24 bits"
            let readable raw = NameColor.create raw |> ok |> NameColor.readable
            Expect.isTrue (readable 0xFFFFFFu) "white reads on the dark chat"
            Expect.isTrue (readable 0xFF0000u) "pure red reads"
            Expect.isFalse (readable 0x0000FFu) "pure blue is too dark"
            Expect.isFalse (readable 0x000000u) "black is too dark"
            Expect.isFalse (readable 0x666666u) "dark grey is too dark"
            Expect.isTrue (readable 0x707070u) "a lighter grey reads"
            Expect.equal NameColor.palette.Length 16 "the palette of the migration"
            for color in NameColor.palette do
                Expect.isTrue (NameColor.readable color) $"palette color {NameColor.value color:X6} reads"
            Expect.equal (Array.distinct NameColor.palette).Length NameColor.palette.Length "palette colors differ"

            let source = Random 7
            for _ in 1 .. 100 do
                Expect.contains NameColor.palette (NameColor.random source) "a random color comes from the palette"

        testCase "character name preserves the game text exactly" <| fun _ ->
            let original = "  Ne\u0301revar  "
            let actual = CharacterName.create 128 original |> ok |> CharacterName.value
            actual.Should().Be(original) |> ignore

        testCase "chat text preserves whitespace and newlines but rejects NUL" <| fun _ ->
            let original = "  first\r\n\tsecond e\u0301  "
            let actual = ChatMessageText.create 128 original |> ok |> ChatMessageText.value
            actual.Should().Be(original) |> ignore
            Expect.isError (ChatMessageText.create 128 "before\000after") "NUL is not chat text"

        testCase "null and malformed UTF-16 return domain errors without throwing" <| fun _ ->
            let highSurrogate = String [| char 0xD800 |]
            let lowSurrogate = String [| char 0xDC00 |]
            for input in [ null; highSurrogate; lowSurrogate; "a" + highSurrogate + "b" ] do
                Expect.isError (DisplayName.create 64 input) "Display name must reject invalid input"
                Expect.isError (CharacterName.create 64 input) "Character name must reject invalid input"
                Expect.isError (ChatMessageText.create 64 input) "Chat text must reject invalid input"

        testCase "name controls cannot disappear through trimming" <| fun _ ->
            for input in [ "\tname"; "name\n"; "name\u2028"; "name\u2029" ] do
                Expect.isError (DisplayName.create 64 input) "Single-line name must reject controls"

        testCase "text factories enforce positive limits and nonblank content" <| fun _ ->
            Expect.isError (Username.create 0 "user") "Limit must be positive"
            Expect.isError (DisplayName.create 64 "   ") "Name must not be blank"
            Expect.isError (ChatMessageText.create 64 "\r\n\t") "Message must not be blank"

        testCase "plugin filename case does not change FormKey identity" <| fun _ ->
            let left = formKey "Skyrim.ESM" 0x3Cu
            let right = formKey "skyrim.esm" 0x3Cu
            left.Should().Be(right) |> ignore
            Expect.notEqual left (formKey " Skyrim.ESM" 0x3Cu) "Leading filename spaces are part of identity"

        testCase "plugin identity preserves non-ASCII code points and normalization form" <| fun _ ->
            let composed = PluginName.create 255 "café.ESP" |> ok
            let decomposed = PluginName.create 255 "cafe\u0301.esp" |> ok
            Expect.notEqual composed decomposed "Filename identity must not merge distinct Unicode sequences"
            let actual = PluginName.create 255 "ÉП.ESP" |> ok |> PluginName.value
            actual.Should().Be("ÉП.esp") |> ignore

        testCase "plugin identity is a bounded opaque key rather than a filename policy" <| fun _ ->
            let original = "  Custom.Record  "
            PluginName.create 255 original |> ok |> PluginName.value
            |> fun value -> value.Should().Be("  custom.record  ") |> ignore
            for input in [ null; ""; "   "; "Plugin\000Name"; "Plugin\nName"; String [| char 0xD800 |] ] do
                Expect.isError (PluginName.create 255 input) "Identity still needs bounded valid text"
            Expect.isError (PluginName.create 3 "Game") "Identity cannot exceed the configured limit"

        testCase "game display labels preserve exact text including empty and blank labels" <| fun _ ->
            for original in [ ""; "   "; "  Ne\u0301revar  "; "\U0001F600" ] do
                LocationName.create 128 original |> ok |> LocationName.value
                |> fun value -> value.Should().Be(original) |> ignore
                ActorValueName.create 128 original |> ok |> ActorValueName.value
                |> fun value -> value.Should().Be(original) |> ignore

        testCase "game display labels reject malformed controls and over-limit text" <| fun _ ->
            for input in [ null; "\000"; "name\n"; "name\u2028"; String [| char 0xD800 |]; String [| char 0xDC00 |] ] do
                Expect.isError (LocationName.create 128 input) "Location label must be safe text"
                Expect.isError (ActorValueName.create 128 input) "Actor value label must be safe text"
            LocationName.create 1 "\U0001F600" |> ok |> ignore
            ActorValueName.create 1 "\U0001F600" |> ok |> ignore
            Expect.isError (LocationName.create 1 "\U0001F600a") "Location label limit counts scalars"
            Expect.isError (ActorValueName.create 1 "\U0001F600a") "Actor value label limit counts scalars"
            Expect.isError (LocationName.create 0 "") "Limit must be positive even for an empty label"
            Expect.isError (ActorValueName.create 0 "") "Limit must be positive even for an empty label"

        testCase "local form and server IDs reject sentinel and load-order values" <| fun _ ->
            LocalFormId.create 0xFFFFFFu |> ok |> LocalFormId.value
            |> fun value -> value.Should().Be(0xFFFFFFu) |> ignore
            Expect.isError (LocalFormId.create 0u) "Zero is not a record identity"
            Expect.isError (LocalFormId.create 0x01000001u) "Load-order bits must already be removed"
            Expect.isError (PlayerId.create 0UL) "Player zero is reserved"
            Expect.isError (ChatChannelId.create 0UL) "Channel zero is reserved"
            Expect.isError (ChatMessageId.create 0UL) "Message zero is reserved"
            PlayerId.create UInt64.MaxValue |> ok |> PlayerId.value
            |> fun value -> value.Should().Be(UInt64.MaxValue) |> ignore

        testCase "actor value keys canonicalize case and keep namespaces distinct" <| fun _ ->
            let builtin = actorKey "Skyrim:Health"
            builtin.Should().Be(actorKey "skyrim:health") |> ignore
            Expect.notEqual builtin (actorKey "avg:health") "Provider participates in identity"

        testCase "actor value key normalization preserves non-ASCII machine names" <| fun _ ->
            Expect.notEqual (actorKey "AVG:café") (actorKey "avg:cafe\u0301") "Do not merge different registered names"
            let actual = actorKey "AVG:ÉПHealth" |> ActorValueKey.value
            actual.Should().Be("avg:ÉПhealth") |> ignore

        testCase "actor value keys require namespace and machine name" <| fun _ ->
            for input in [ "health"; ":health"; "avg:"; "av g:health"; "avg: health"; " Skyrim:Health " ] do
                Expect.isError (ActorValueKey.create 128 input) (sprintf "Reject %A" input)
    ]

let private spatialTests =
    testList "native coordinates" [
        testCase "coordinates and camera directions preserve finite native values" <| fun _ ->
            let position = Position.create -123.5f 0.0f 42.25f |> ok
            let cameraDirection = CameraDirection.create -12.0f 40.0f 0.5f |> ok
            WorldUnit.value position.X |> fun value -> value.Should().Be(-123.5f) |> ignore
            cameraDirection.X |> fun value -> value.Should().Be(-12.0f) |> ignore
            cameraDirection.Y |> fun value -> value.Should().Be(40.0f) |> ignore

        testCase "positions and camera directions reject every non-finite component" <| fun _ ->
            for bad in [ Single.NaN; Single.PositiveInfinity; Single.NegativeInfinity ] do
                for x, y, z in [ bad, 0.0f, 0.0f; 0.0f, bad, 0.0f; 0.0f, 0.0f, bad ] do
                    Expect.isError (Position.create x y z) "Reject invalid position"
                    Expect.isError (CameraDirection.create x y z) "Reject invalid cameraDirection"

        testCase "distance promotes coordinates before subtraction and squaring" <| fun _ ->
            let left = Position.create Single.MaxValue 0.0f 0.0f |> ok
            let right = Position.create -Single.MaxValue 0.0f 0.0f |> ok
            let distance = Position.distance left right |> float
            Expect.isTrue (Double.IsFinite distance) "Finite float32 positions must not overflow their distance"
            distance.Should().Be(2.0 * float Single.MaxValue) |> ignore
            let nearby = Position.create 3.0f 4.0f 0.0f |> ok
            (Position.distanceSquared Position.zero nearby |> float).Should().Be(25.0) |> ignore

        testCase "space identity uses FormKey, not the player's display label" <| fun _ ->
            let key = formKey "Skyrim.esm" 0x3Cu
            let left = location key "Whiterun" Position.zero
            let right = location key "Вайтран" Position.zero
            let elsewhere = location (formKey "Skyrim.esm" 0x3Du) "Whiterun" Position.zero
            Expect.isTrue (PlayerLocation.isSameSpace left right) "Localization must not split a coordinate space"
            Expect.equal (PlayerLocation.tryDistance left elsewhere) ValueNone "Equal XYZ in different spaces have no distance"

        testCase "radius includes its boundary and zero only includes coincident points" <| fun _ ->
            let key = formKey "Skyrim.esm" 0x3Cu
            let origin = location key "Tamriel" Position.zero
            let target = location key "Whiterun" (Position.create 3.0f 4.0f 0.0f |> ok)
            Expect.equal (PlayerLocation.isWithinRadius (WorldUnit.create 5.0f |> ok) origin target) (Ok true) "Boundary is included"
            Expect.equal (PlayerLocation.isWithinRadius (WorldUnit.create 0.0f |> ok) origin origin) (Ok true) "Same position"
            Expect.equal (PlayerLocation.isWithinRadius (WorldUnit.create 0.0f |> ok) origin target) (Ok false) "Other position"
            let elsewhere = location (formKey "Other.esp" 0x3Cu) "Tamriel" Position.zero
            Expect.equal (PlayerLocation.isWithinRadius (WorldUnit.create 100.0f |> ok) origin elsewhere) (Ok false) "Different space"

        testCase "radius rejects negative and non-finite inputs" <| fun _ ->
            let origin = location (formKey "Skyrim.esm" 0x3Cu) "Tamriel" Position.zero
            for value in [ -1.0f; Single.NaN; Single.PositiveInfinity ] do
                let radius = LanguagePrimitives.Float32WithMeasure<worldUnit> value
                Expect.equal (PlayerLocation.isWithinRadius radius origin origin) (Error DomainError.InvalidRadius) "Invalid radius"
    ]

let private stateTests =
    testList "actor-owned player state" [
        testCase "resources preserve negative and above-maximum observations" <| fun _ ->
            for current, maximum in [ -5, 100; 120, 100; 1, -1; Int32.MinValue, Int32.MaxValue ] do
                let state = ActorValueState.resource current maximum
                let observed = ActorValueState.fold (fun _ -> failtest "Expected a resource") (fun current maximum -> current, maximum) state
                Expect.equal observed (current, maximum) "Neither part is clamped or recomputed"

        testCase "scalars reject non-finite readings and stay a shape of their own" <| fun _ ->
            let scalar = ActorValueState.scalar 42.0f |> ok
            let observed = ActorValueState.fold (fun value -> ValueSome (ActorValue.value value)) (fun _ _ -> ValueNone) scalar
            Expect.equal observed (ValueSome 42.0f) "Scalar has a value and no maximum"
            Expect.notEqual scalar (ActorValueState.resource 42 42) "A scalar is not a resource of the same amount"
            for bad in [ Single.NaN; Single.PositiveInfinity; Single.NegativeInfinity ] do
                Expect.isError (ActorValueState.scalar bad) "Scalar must be finite"

        testCase "storage reuses immutable projections and invalidates every mutation" <| fun _ ->
            let key = actorKey "av:health"
            let other = actorKey "av:magicka"
            let original = Map.ofList [key, health 50]
            let storage = ActorValueStorage.ofSnapshot original
            let snapshot () = ActorValueStorage.snapshot storage

            Expect.isTrue (obj.ReferenceEquals(original, snapshot ())) "The supplied immutable projection is reusable"
            ActorValueStorage.set key (health 25) storage
            let changed = snapshot ()
            Expect.equal changed[key] (health 25) "set invalidates the cache"
            Expect.isTrue (obj.ReferenceEquals(changed, snapshot ())) "Repeated snapshots reuse their map"
            Expect.equal original[key] (health 50) "Previously published map stays immutable"

            ActorValueStorage.setMany [| key, health 10; other, health 30 |] storage
            Expect.equal (snapshot () |> Map.count) 2 "setMany invalidates the cache"

            ActorValueStorage.remove key storage |> ignore
            Expect.isFalse (snapshot () |> Map.containsKey key) "remove invalidates the cache"

            ActorValueStorage.clear storage
            Expect.isTrue (snapshot () |> Map.isEmpty) "clear publishes an empty map"
            Expect.equal changed[key] (health 25) "Intermediate snapshots also stay detached"

        testCase "storage snapshots detach from later updates and removals" <| fun _ ->
            let storage = ActorValueStorage.create ()
            let key = actorKey "skyrim:health"
            ActorValueStorage.set key (health 50) storage
            let before = ActorValueStorage.snapshot storage
            ActorValueStorage.set key (health 25) storage
            ActorValueStorage.remove key storage |> ignore
            Expect.equal (ActorValueStorage.count storage) 0 "Live key is gone"
            Expect.equal before[key] (health 50) "Snapshot keeps the original reading"

        testCase "setMany merges by key and last duplicate wins" <| fun _ ->
            let storage = ActorValueStorage.create ()
            let key = actorKey "skyrim:health"
            let rareKey = actorKey "avg:rare"
            ActorValueStorage.set rareKey (health 7) storage
            ActorValueStorage.setMany [| key, health 10; key, health 20 |] storage
            Expect.equal (ActorValueStorage.count storage) 2 "Unchanged readings are retained"
            Expect.equal (ActorValueStorage.tryFind key storage) (ValueSome (health 20)) "Last update wins"

        testCase "player snapshots detach nested mutable actor values" <| fun _ ->
            let player = Player.create (profile 1UL "First")
            let key = actorKey "skyrim:health"
            Player.setActorValue key (health 50) player
            let before = Player.snapshot player
            Player.setActorValue key (health 5) player
            Expect.equal before.ActorValues[key] (health 50) "Nested state must be detached too"

        testCase "starting another character clears telemetry even for an identical name" <| fun _ ->
            let name = characterName "Nerevar"
            let player = Player.create (profile 1UL "First") |> Player.withCharacterName name
            let located = player |> Player.withLocation (location (formKey "Skyrim.esm" 0x3Cu) "Tamriel" Position.zero)
            let key = actorKey "skyrim:health"
            Player.setActorValue key (health 50) located
            let next = Player.beginCharacter name located
            Expect.equal next.CharacterName (ValueSome name) "New character is named"
            Expect.equal next.Location ValueNone "Previous save's coordinates are cleared"
            Expect.equal (Player.actorValueCount next) 0 "Previous save's stats are cleared"
            Player.setActorValue key (health 80) next
            Expect.equal (Player.tryFindActorValue key located) (ValueSome (health 50)) "Old storage is not reused"

        testCase "leaving the world preserves profile but clears character telemetry" <| fun _ ->
            let player =
                Player.create (profile 1UL "First")
                |> Player.beginCharacter (characterName "Nerevar")
                |> Player.withLocation (location (formKey "Skyrim.esm" 0x3Cu) "Tamriel" Position.zero)
            Player.setActorValue (actorKey "skyrim:health") (health 50) player
            let cleared = Player.clearGameState player
            Expect.equal cleared.Data player.Data "Server identity survives"
            Expect.equal cleared.CharacterName ValueNone "No active character"
            Expect.equal cleared.Location ValueNone "No active coordinates"
            Expect.equal (Player.actorValueCount cleared) 0 "No old stats"

        testCase "sample replacement removes absent values without mutating the previous state" <| fun _ ->
            let name = characterName "Nerevar"
            let healthKey = actorKey "skyrim:health"
            let extraKey = actorKey "avg:extra"
            let original = Player.create (profile 1UL "First") |> Player.applyUpdate (PlayerUpdate.BeginCharacter name)
            let place = location (formKey "Skyrim.esm" 0x3Cu) "Tamriel" Position.zero
            let first =
                original
                |> Player.applyUpdate (PlayerUpdate.SetLocation(1UL, ValueSome place))
                |> Player.applyUpdate (PlayerUpdate.SetActorValues(Map.ofList [healthKey, health 120; extraKey, health 5]))

            let before = Player.snapshot first
            let valuesOnly = first |> Player.applyUpdate (PlayerUpdate.SetActorValues Map.empty)
            Expect.equal valuesOnly.Location first.Location "Values do not touch movement."
            let movedOnly = first |> Player.applyUpdate (PlayerUpdate.SetLocation(2UL, ValueNone))
            Expect.equal (Player.actorValuesSnapshot movedOnly) before.ActorValues "Movement does not touch values."
            let second =
                first
                |> Player.applyUpdate (PlayerUpdate.SetLocation(2UL, ValueNone))
                |> Player.applyUpdate (PlayerUpdate.SetActorValues(Map.ofList [healthKey, health -5]))

            Expect.equal second.Location ValueNone "unknown position replaces a previous known location"
            Expect.equal (Player.actorValueCount second) 1 "a missing key is removed, not retained forever"
            Expect.equal (Player.tryFindActorValue extraKey second) ValueNone "full sample replaces storage"
            Expect.equal (Player.snapshot first) before "previous storage is detached from the replacement"
            Expect.equal second.CharacterGeneration first.CharacterGeneration "samples do not switch character"

        testCase "character generation and metadata follow explicit lifecycle operations" <| fun _ ->
            let original = Player.create (profile 1UL "First")
            let name = characterName "Nerevar"
            let details = PlayerDetails.create ValueNone (ValueSome 80u) PlayerActivity.unknown ValueNone ValueNone
            let first = original |> Player.applyUpdate (PlayerUpdate.BeginCharacter name)
            let sampled = first |> Player.applyUpdate (PlayerUpdate.SetDetails details)
            let renamed = sampled |> Player.applyUpdate (PlayerUpdate.RenameCharacter (characterName "Renamed"))

            Expect.equal original.CharacterGeneration 0UL "initial connection has no character generation"
            Expect.equal first.CharacterGeneration 1UL "first character starts a generation"
            Expect.equal renamed.CharacterGeneration 1UL "rename retains generation"
            Expect.equal renamed.Details details "rename retains metadata"

            let restarted = renamed |> Player.applyUpdate (PlayerUpdate.BeginCharacter (characterName "Renamed"))
            Expect.equal restarted.CharacterGeneration 2UL "same-name save switches are visible"
            Expect.equal restarted.Details PlayerDetails.empty "race, level, activity and place do not leak across saves"

            let left = restarted |> Player.applyUpdate PlayerUpdate.LeaveGame
            Expect.equal left.CharacterGeneration 3UL "leaving ends the active generation"
            Expect.equal left.CharacterName ValueNone "no active character"
            Expect.equal left.Data original.Data "account profile survives game lifecycle"

        testCase "profile updates preserve player identity" <| fun _ ->
            let player = Player.create (profile 1UL "First")
            let nextProfile = PlayerData.withDisplayName (displayName "Renamed") player.Data
            let renamed = Player.withProfile nextProfile player |> ok
            Expect.equal renamed.Data.PlayerId player.Data.PlayerId "Rename keeps identity"
            Expect.equal (Player.withProfile (profile 2UL "Other") player |> Result.map Player.snapshot)
                (Error DomainError.PlayerIdentityMismatch) "Cannot replace player identity"
    ]

let private patchTests =
    let healthKey, staminaKey, magickaKey = actorKey "skyrim:health", actorKey "skyrim:stamina", actorKey "skyrim:magicka"
    testList "actor value patches" [
        testCase "new and changed readings are set while unchanged ones are left out" <| fun _ ->
            let previous = Map.ofList [ healthKey, health 50; staminaKey, reading "Stamina" 80 ]
            let latest = Map.ofList [ healthKey, health -20; staminaKey, reading "Stamina" 80; magickaKey, reading "Magicka" 10 ]
            Expect.equal (ActorValuesPatch.between previous latest)
                (ValueSome { Removed = []; Set = [ healthKey, health -20; magickaKey, reading "Magicka" 10 ] })
                "A value change keeps its kind; a negative reading travels as it is"

        testCase "a removed key is listed under the label it had" <| fun _ ->
            let previous = Map.ofList [ healthKey, health 50; staminaKey, reading "Stamina" 80 ]
            let latest = Map.ofList [ healthKey, health 50 ]
            Expect.equal (ActorValuesPatch.between previous latest)
                (ValueSome { Removed = [ struct (staminaKey, label "Stamina") ]; Set = [] }) "Only the missing kind"
            Expect.equal (ActorValuesPatch.between previous Map.empty)
                (ValueSome { Removed = [ struct (healthKey, label "Health"); struct (staminaKey, label "Stamina") ]; Set = [] })
                "Clearing every reading removes every kind"

        testCase "a changed label removes the old kind and sets the new one" <| fun _ ->
            let previous = Map.ofList [ healthKey, health 50 ]
            let latest = Map.ofList [ healthKey, reading "Здоровье" 50 ]
            Expect.equal (ActorValuesPatch.between previous latest)
                (ValueSome { Removed = [ struct (healthKey, label "Health") ]; Set = [ healthKey, reading "Здоровье" 50 ] })
                "A kind is the key with its label"

        testCase "equal readings make no patch" <| fun _ ->
            let values = Map.ofList [ healthKey, health 50; staminaKey, reading "Stamina" 80 ]
            Expect.equal (ActorValuesPatch.between values (Map.ofList [ staminaKey, reading "Stamina" 80; healthKey, health 50 ])) ValueNone "Same readings"
            Expect.equal (ActorValuesPatch.between Map.empty Map.empty) ValueNone "Nothing to nothing"
    ]

let private chatTests =
    testList "bounded chat history" [
        testCase "membership uses identity and survives a display-name change" <| fun _ ->
            let value = chat 2
            Expect.isFalse (Chat.join (playerId 1UL) value) "Joining twice does not duplicate membership"
            Chat.append (message 1UL 1UL (profile 1UL "Renamed")) value |> ok
            Expect.equal (Chat.memberCount value) 1 "One identity remains one member"
            Expect.equal (Chat.messageCount value) 1 "Renamed profile may send"

        testCase "wrong channel and nonmember errors leave history unchanged" <| fun _ ->
            let value = chat 2
            let before = Chat.snapshot value
            Expect.equal (Chat.append (message 2UL 100UL (profile 1UL "First")) value)
                (Error DomainError.ChannelMismatch) "Wrong channel"
            Expect.equal (Chat.append (message 1UL 100UL (profile 2UL "Other")) value)
                (Error (DomainError.NotChatMember (playerId 2UL))) "Not a member"
            Expect.equal (Chat.snapshot value) before "Failed appends have no side effects"
            append 1UL value

        testCase "bounded history evicts the oldest messages in FIFO order" <| fun _ ->
            let value = chat 2
            for id in [ 10UL; 20UL; 40UL ] do
                append id value

            let page = Chat.historyAfter ValueNone 10 value |> ok
            Expect.equal (historyIds page) [ 20UL; 40UL ] "Only the newest capacity messages remain"
            Expect.isFalse page.HasGap "A fresh read has no prior cursor to lose"

        testCase "pagination continues strictly after the last returned ID" <| fun _ ->
            let value = chat 4
            for id in [ 10UL; 20UL; 40UL ] do
                append id value

            let first = Chat.historyAfter ValueNone 2 value |> ok
            let second = Chat.historyAfter first.NextCursor 2 value |> ok
            Expect.equal (historyIds first) [ 10UL; 20UL ] "First page"
            Expect.isTrue first.HasMore "Another page exists"
            Expect.equal (historyIds second) [ 40UL ] "No duplicate boundary message"
            Expect.isFalse second.HasMore "Tail is exhausted"
            Expect.equal second.NextCursor (ValueSome (messageId 40UL)) "Cursor reaches the last message"

        testCase "gap detection tracks actual evictions, not gaps between global IDs" <| fun _ ->
            let value = chat 2
            for id in [ 10UL; 20UL; 40UL ] do
                append id value

            let lost = Chat.historyAfter (ValueSome (messageId 5UL)) 10 value |> ok
            let caughtUp = Chat.historyAfter (ValueSome (messageId 10UL)) 10 value |> ok
            let globalGap = Chat.historyAfter (ValueSome (messageId 15UL)) 10 value |> ok
            Expect.isTrue lost.HasGap "Message 10 was missed and evicted"
            Expect.isFalse caughtUp.HasGap "Evicted cursor was already observed"
            Expect.isFalse globalGap.HasGap "IDs 11..19 may belong to another channel"

        testCase "clearing history preserves ordering and gap tracking" <| fun _ ->
            let value = chat 2
            append 10UL value
            append 20UL value
            Chat.clearHistory value
            Expect.isError (Chat.append (message 1UL 20UL (profile 1UL "First")) value) "Accepted IDs cannot be reused after clear"
            let empty = Chat.historyAfter (ValueSome (messageId 10UL)) 10 value |> ok
            Expect.equal empty.Messages [] "History was cleared"
            Expect.isTrue empty.HasGap "Unread cleared message was lost"
            Expect.equal empty.NextCursor (ValueSome (messageId 10UL)) "Empty page retains supplied cursor"
            append 30UL value

        testCase "a removed message leaves no gap and its ID is not accepted again" <| fun _ ->
            let value = chat 3
            append 10UL value
            append 20UL value
            append 30UL value
            Expect.equal (Chat.remove (messageId 20UL) value |> ValueOption.map _.MessageId) (ValueSome (messageId 20UL)) "The removed message is returned"
            Expect.equal (Chat.remove (messageId 20UL) value) ValueNone "It is gone"
            let page = Chat.historyAfter (ValueSome (messageId 10UL)) 10 value |> ok
            Expect.equal (historyIds page) [ 30UL ] "Only the kept message follows"
            Expect.isFalse page.HasGap "Nothing a reader should fetch was lost"
            Expect.isError (Chat.append (message 1UL 20UL (profile 1UL "First")) value) "Removed IDs cannot be reused"

        testCase "snapshot and pages remain stable after mutation" <| fun _ ->
            let value = chat 2
            append 10UL value
            let before = Chat.snapshot value
            let page = Chat.historyAfter ValueNone 10 value |> ok
            Chat.leave (playerId 1UL) value |> ignore
            Chat.clearHistory value
            Expect.equal before.Players (Set.singleton (playerId 1UL)) "Membership snapshot is detached"
            Expect.equal (historyIds page) [ 10UL ] "History page is detached"
            Expect.equal before.Messages page.Messages "Both keep the original message"

        testCase "checked guild history capacity constructs a correctly bound chat without revalidation" <| fun _ ->
            Expect.isError (ChatHistoryCapacity.create 0) "Zero history is refused at preflight."
            Expect.isError (ChatHistoryCapacity.create -1) "Negative history is refused at preflight."
            let capacity = ChatHistoryCapacity.create 2 |> ok
            let guildId = GuildId.create 7UL |> ok
            let guildChat = Chat.createGuild capacity guildId
            Expect.equal guildChat.Kind ChatChannelKind.Guild "Guild kind matches its checked ID."
            Expect.equal guildChat.ChannelId (ChatChannels.ofGuild guildId) "Guild channel derives from that same ID."

        testCase "history and channel limits are validated" <| fun _ ->
            Expect.isError (Chat.create ChatChannels.globalId ChatChannelKind.Global 0) "No unbounded/zero history"
            Expect.isError (Chat.historyAfter ValueNone 0 (chat 2)) "Page size must be positive"
            let value = chat 2
            append 10UL value
            Expect.isError (Chat.append (message 1UL 9UL (profile 1UL "First")) value) "Out-of-order ID rejected"
            let future = Chat.historyAfter (ValueSome (messageId 100UL)) 10 value |> ok
            Expect.equal future.Messages [] "Future cursor returns an empty page"
            Expect.equal future.NextCursor (ValueSome (messageId 100UL)) "Future cursor is retained"

        testCase "message timestamp is UTC and author remains the send-time profile" <| fun _ ->
            let author = profile 1UL "Original"
            let instant = DateTimeOffset(2026, 9, 26, 2, 0, 0, TimeSpan.FromHours 7.0)
            let value = ChatMessage.create (messageId 1UL) (channelId 1UL) (PublicIdentity.Profile author) ValueNone
                            (ChatMessageText.create 128 "Hello" |> ok) instant
            let renamed = PlayerData.withDisplayName (displayName "Renamed") author
            Expect.equal value.SentAt.Offset TimeSpan.Zero "Wire-facing timestamp is canonical UTC"
            Expect.equal value.SentAt instant "The instant is preserved"
            Expect.equal value.Author (ValueSome (PublicIdentity.Profile author)) "Message keeps the immutable send-time author"
            Expect.notEqual value.Author (ValueSome (PublicIdentity.Profile renamed)) "Renaming profile does not rewrite message"
    ]

let private movementTests = testList "Movement" [
    testCase "sample sequence is independent of zero source timestamp and cannot establish context" <| fun _ ->
        let player = Player.create (profile 1UL "First")
        let place = location (formKey "Skyrim.esm" 0x3Cu) "Tamriel" Position.zero
        let sample =
            { ContextRevision = 1UL
              Sequence = 1UL
              Pose = MovementPose.ofLocation place }
        Expect.equal (Player.tryApplyMovement sample player |> ValueOption.map Player.snapshot) ValueNone "No location baseline."

        let located = player |> Player.applyUpdate (PlayerUpdate.SetLocation(1UL, ValueSome place))
        let moved = Player.tryApplyMovement sample located |> ValueOption.get
        Expect.equal moved.MovementSequence 1UL "Zero timestamp is still an ordered sample."
        Expect.equal (Player.tryApplyMovement sample moved |> ValueOption.map Player.snapshot) ValueNone "Duplicate sequence is ignored."

        let cleared = Player.clearGameState moved
        Expect.equal cleared.MovementHighWater 1UL "Character changes preserve transition highwater."
        Expect.equal cleared.MovementContext 0UL "Old samples are disabled."
        Expect.equal (Player.tryApplyMovement { sample with Sequence = 2UL } cleared |> ValueOption.map Player.snapshot) ValueNone "Late sample cannot revive a character."
]

let tests =
    testList "Dreamsleeve.Server.Domain" [ textTests; spatialTests; stateTests; patchTests; chatTests; movementTests ]
