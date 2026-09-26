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
    PlayerData.create (playerId id) (Username.create 32 (sprintf "user%d" id) |> ok) (displayName name)

let private formKey plugin id =
    FormKey.create (PluginName.create 255 plugin |> ok) (LocalFormId.create id |> ok)

let private location key name position =
    PlayerLocation.create (Location.create key (LocationName.create 128 name |> ok)) position Rotation.zero

let private health amount =
    ActorValueInfo.create (ActorValueName.create 64 "Health" |> ok) (ActorValueState.resource amount 100.0f |> ok)

let private chat capacity =
    let value = Chat.create (channelId 1UL) capacity |> ok
    Chat.join (playerId 1UL) value |> ignore
    value

let private message channel id author =
    ChatMessage.create (messageId id) (channelId channel) author
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
        testCase "coordinates and unwrapped radians preserve finite native values" <| fun _ ->
            let position = Position.create -123.5f 0.0f 42.25f |> ok
            let rotation = Rotation.create -12.0f 40.0f 0.5f |> ok
            WorldUnit.value position.X |> fun value -> value.Should().Be(-123.5f) |> ignore
            Radian.value rotation.X |> fun value -> value.Should().Be(-12.0f) |> ignore
            Radian.value rotation.Y |> fun value -> value.Should().Be(40.0f) |> ignore

        testCase "positions and rotations reject every non-finite component" <| fun _ ->
            for bad in [ Single.NaN; Single.PositiveInfinity; Single.NegativeInfinity ] do
                for x, y, z in [ bad, 0.0f, 0.0f; 0.0f, bad, 0.0f; 0.0f, 0.0f, bad ] do
                    Expect.isError (Position.create x y z) "Reject invalid position"
                    Expect.isError (Rotation.create x y z) "Reject invalid rotation"

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
            for current, maximum in [ -5.0f, 100.0f; 120.0f, 100.0f; 1.0f, -1.0f ] do
                let state = ActorValueState.resource current maximum |> ok
                (ActorValueState.current state |> ActorValue.value).Should().Be(current) |> ignore
                Expect.equal (ActorValueState.tryMaximum state |> ValueOption.map ActorValue.value) (ValueSome maximum) "Maximum is not recomputed"

        testCase "resource updates preserve their shape while rejecting non-finite readings" <| fun _ ->
            let resource = ActorValueState.resource 30.0f 80.0f |> ok
            let changed = ActorValueState.withCurrent -5.0f resource |> ok
            Expect.equal (ActorValueState.tryMaximum changed |> ValueOption.map ActorValue.value) (ValueSome 80.0f) "Maximum survives current update"
            Expect.equal (ActorValueState.scalar 42.0f |> ok |> ActorValueState.tryMaximum) ValueNone "Scalar has no maximum"
            for bad in [ Single.NaN; Single.PositiveInfinity; Single.NegativeInfinity ] do
                Expect.isError (ActorValueState.scalar bad) "Scalar must be finite"
                Expect.isError (ActorValueState.resource bad 100.0f) "Current must be finite"
                Expect.isError (ActorValueState.resource 100.0f bad) "Maximum must be finite"

        testCase "storage snapshots detach from later updates and removals" <| fun _ ->
            let storage = ActorValueStorage.create ()
            let key = actorKey "skyrim:health"
            ActorValueStorage.set key (health 50.0f) storage
            let before = ActorValueStorage.snapshot storage
            ActorValueStorage.set key (health 25.0f) storage
            ActorValueStorage.remove key storage |> ignore
            Expect.equal (ActorValueStorage.count storage) 0 "Live key is gone"
            Expect.equal before[key] (health 50.0f) "Snapshot keeps the original reading"

        testCase "setMany merges by key and last duplicate wins" <| fun _ ->
            let storage = ActorValueStorage.create ()
            let key = actorKey "skyrim:health"
            let rareKey = actorKey "avg:rare"
            ActorValueStorage.set rareKey (health 7.0f) storage
            ActorValueStorage.setMany [| key, health 10.0f; key, health 20.0f |] storage
            Expect.equal (ActorValueStorage.count storage) 2 "Unchanged readings are retained"
            Expect.equal (ActorValueStorage.tryFind key storage) (ValueSome (health 20.0f)) "Last update wins"

        testCase "player snapshots detach nested mutable actor values" <| fun _ ->
            let player = Player.create (profile 1UL "First")
            let key = actorKey "skyrim:health"
            Player.setActorValue key (health 50.0f) player
            let before = Player.snapshot player
            Player.setActorValue key (health 5.0f) player
            Expect.equal before.ActorValues[key] (health 50.0f) "Nested state must be detached too"

        testCase "starting another character clears telemetry even for an identical name" <| fun _ ->
            let name = characterName "Nerevar"
            let player = Player.create (profile 1UL "First") |> Player.withCharacterName name
            let located = player |> Player.withLocation (location (formKey "Skyrim.esm" 0x3Cu) "Tamriel" Position.zero)
            let key = actorKey "skyrim:health"
            Player.setActorValue key (health 50.0f) located
            let next = Player.beginCharacter name located
            Expect.equal next.CharacterName (ValueSome name) "New character is named"
            Expect.equal next.Location ValueNone "Previous save's coordinates are cleared"
            Expect.equal (Player.actorValueCount next) 0 "Previous save's stats are cleared"
            Player.setActorValue key (health 80.0f) next
            Expect.equal (Player.tryFindActorValue key located) (ValueSome (health 50.0f)) "Old storage is not reused"

        testCase "leaving the world preserves profile but clears character telemetry" <| fun _ ->
            let player =
                Player.create (profile 1UL "First")
                |> Player.beginCharacter (characterName "Nerevar")
                |> Player.withLocation (location (formKey "Skyrim.esm" 0x3Cu) "Tamriel" Position.zero)
            Player.setActorValue (actorKey "skyrim:health") (health 50.0f) player
            let cleared = Player.clearGameState player
            Expect.equal cleared.Data player.Data "Server identity survives"
            Expect.equal cleared.CharacterName ValueNone "No active character"
            Expect.equal cleared.Location ValueNone "No active coordinates"
            Expect.equal (Player.actorValueCount cleared) 0 "No old stats"

        testCase "profile updates preserve player identity" <| fun _ ->
            let player = Player.create (profile 1UL "First")
            let nextProfile = PlayerData.withDisplayName (displayName "Renamed") player.Data
            let renamed = Player.withProfile nextProfile player |> ok
            Expect.equal renamed.Data.PlayerId player.Data.PlayerId "Rename keeps identity"
            Expect.equal (Player.withProfile (profile 2UL "Other") player |> Result.map Player.snapshot)
                (Error DomainError.PlayerIdentityMismatch) "Cannot replace player identity"
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
            for id in [ 10UL; 20UL; 40UL ] do append id value
            let page = Chat.historyAfter ValueNone 10 value |> ok
            Expect.equal (historyIds page) [ 20UL; 40UL ] "Only the newest capacity messages remain"
            Expect.isFalse page.HasGap "A fresh read has no prior cursor to lose"

        testCase "pagination continues strictly after the last returned ID" <| fun _ ->
            let value = chat 4
            for id in [ 10UL; 20UL; 40UL ] do append id value
            let first = Chat.historyAfter ValueNone 2 value |> ok
            let second = Chat.historyAfter first.NextCursor 2 value |> ok
            Expect.equal (historyIds first) [ 10UL; 20UL ] "First page"
            Expect.isTrue first.HasMore "Another page exists"
            Expect.equal (historyIds second) [ 40UL ] "No duplicate boundary message"
            Expect.isFalse second.HasMore "Tail is exhausted"
            Expect.equal second.NextCursor (ValueSome (messageId 40UL)) "Cursor reaches the last message"

        testCase "gap detection tracks actual evictions, not gaps between global IDs" <| fun _ ->
            let value = chat 2
            for id in [ 10UL; 20UL; 40UL ] do append id value
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

        testCase "history and channel limits are validated" <| fun _ ->
            Expect.isError (Chat.create (channelId 1UL) 0) "No unbounded/zero history"
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
            let value = ChatMessage.create (messageId 1UL) (channelId 1UL) author
                            (ChatMessageText.create 128 "Hello" |> ok) instant
            let renamed = PlayerData.withDisplayName (displayName "Renamed") author
            Expect.equal value.SentAt.Offset TimeSpan.Zero "Wire-facing timestamp is canonical UTC"
            Expect.equal value.SentAt instant "The instant is preserved"
            Expect.equal value.Author.DisplayName author.DisplayName "Message keeps the immutable send-time author"
            Expect.notEqual value.Author.DisplayName renamed.DisplayName "Renaming profile does not rewrite message"
    ]

let tests =
    testList "Dreamsleeve.Server.Domain" [ textTests; spatialTests; stateTests; chatTests ]
