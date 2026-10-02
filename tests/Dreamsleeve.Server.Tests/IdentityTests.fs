module Dreamsleeve.Server.Tests.IdentityTests

open System
open System.Text
open Expecto
open Dreamsleeve.Server.Domain

let private ok = function
    | Ok value -> value
    | Error error -> failtestf "Expected success, received %A" error

let private pseudonym text = Pseudonym.create text |> ok

let private profile id username display =
    PlayerData.create (PlayerId.create id |> ok) (Username.create 32 username |> ok) (DisplayName.create 64 display |> ok) NameColor.unknown

let private dictionary names =
    PseudonymDictionary.create (names |> List.map pseudonym) |> ValueOption.get

// Always the first entry: collisions and numbering become deterministic.
let private first (_: int) = 0

let private hide pick profile book =
    PseudonymBook.apply pick HiddenIdentity.Everywhere profile book |> ValueOption.get

let private primitiveTests = testList "Pseudonym" [
    testCase "entries follow the client alias rules and are kept exactly" <| fun _ ->
        Expect.equal (Pseudonym.value (pseudonym "Наёмник")) "Наёмник" "Cyrillic entry"
        Expect.equal (Pseudonym.value (pseudonym "Dark Elf")) "Dark Elf" "inner space"
        let longest = String('ж', 24)
        Expect.equal (Encoding.UTF8.GetByteCount longest) 48 "48 bytes of UTF-8"
        Expect.isOk (Pseudonym.create longest) "the bound is inclusive"
        for invalid in [ ""; " "; " Бард"; "Бард "; "<b>Бард</b>"; "Бард\nСтраж"; "a\u0007b"; String('ж', 25) ] do
            Expect.isError (Pseudonym.create invalid) $"refused: {invalid}"

    testCase "numbers tell equal picks apart and stored numbered names read back" <| fun _ ->
        let entry = pseudonym "Страж"
        Expect.equal (Pseudonym.value (Pseudonym.numbered 1 entry)) "Страж" "no number for the first"
        Expect.equal (Pseudonym.value (Pseudonym.numbered 2 entry)) "Страж 2" "short number"
        let numbered = Pseudonym.numbered 1024 (pseudonym (String('ж', 24)))
        Expect.isError (Pseudonym.create (Pseudonym.value numbered)) "longer than an entry"
        Expect.equal (Pseudonym.restore (Pseudonym.value numbered)) (Ok numbered) "storage accepts the number"
]

let private dictionaryTests = testList "PseudonymDictionary" [
    testCase "built-in list matches the client's aliases.toml" <| fun _ ->
        Expect.equal PseudonymDictionary.builtIn.Count 24 "24 names"
        Expect.equal (PseudonymDictionary.builtIn.Names |> List.map Pseudonym.value |> List.head) "Странник" "same order"
        let root = IO.DirectoryInfo(AppContext.BaseDirectory)
        let rec find (directory: IO.DirectoryInfo) =
            let candidate = IO.Path.Combine(directory.FullName, "src", "Dreamsleeve.Client", "aliases.toml")
            if IO.File.Exists candidate then candidate
            elif isNull directory.Parent then failtest "aliases.toml not found"
            else find directory.Parent
        let client = IO.File.ReadAllText(find root)
        for name in PseudonymDictionary.builtIn.Names do
            Expect.stringContains client $"\"{Pseudonym.value name}\"" "the client dictionary has the same name"

    testCase "repeats are dropped without case after NFC and an empty list is absent" <| fun _ ->
        let names = dictionary [ "Страж"; "страж"; "Café"; "Café"; "Бард" ]
        Expect.equal (names.Names |> List.map Pseudonym.value) [ "Страж"; "Café"; "Бард" ] "first spelling kept"
        Expect.isTrue (PseudonymDictionary.create []).IsNone "nothing valid"
]

let private bookTests = testList "PseudonymBook" [
    testCase "a pseudonym comes from the dictionary by pick, never from the player's names" <| fun _ ->
        let book = PseudonymBook.create (dictionary [ "Бард"; "Страж"; "Рыбак" ])
        let mutable asked = 0
        let chosen = hide (fun count -> asked <- count; 2) (profile 1UL "rybak" "Nerevar") book
        Expect.equal asked 3 "pick sees the dictionary size"
        Expect.equal (Pseudonym.value chosen) "Рыбак" "the picked entry"
        let other = PseudonymBook.create (dictionary [ "Бард"; "Страж"; "Рыбак" ])
        let same = hide (fun _ -> 2) (profile 1UL "someone" "Somebody Else") other
        Expect.equal same chosen "real names do not steer the choice"

    testCase "names shown online get a number, compared without case after NFC" <| fun _ ->
        let book = PseudonymBook.create (dictionary [ "Страж" ])
        PseudonymBook.show (profile 1UL "guard" "СТРАЖ") book
        let second = hide first (profile 2UL "b" "B") book
        Expect.equal (Pseudonym.value second) "Страж 2" "a display name online takes the plain name"
        let third = hide first (profile 3UL "c" "C") book
        Expect.equal (Pseudonym.value third) "Страж 3" "other pseudonyms count too"
        PseudonymBook.release (PlayerId.create 2UL |> ok) book
        let fourth = hide first (profile 4UL "d" "D") book
        Expect.equal (Pseudonym.value fourth) "Страж 2" "a released pseudonym is free again"

    testCase "the player's own real names are never handed back as a pseudonym" <| fun _ ->
        let book = PseudonymBook.create (dictionary [ "Страж" ])
        let own = hide first (profile 1UL "user1" "страж") book
        Expect.equal (Pseudonym.value own) "Страж 2" "own display name counts as shown"
        Expect.equal (PseudonymBook.tryFind (PlayerId.create 1UL |> ok) book) (ValueSome own) "registered"

    testCase "every switch to hidden picks anew and showing restores the profile" <| fun _ ->
        let book = PseudonymBook.create (dictionary [ "Бард"; "Страж" ])
        let player = profile 5UL "user5" "Nerevar"
        let mutable next = 0
        let pick _ = next <- next + 1; next - 1
        let before = hide pick player book
        PseudonymBook.show player book
        Expect.isTrue (PseudonymBook.tryFind player.PlayerId book).IsNone "shown again"
        Expect.equal (PseudonymBook.tryProfile player.PlayerId book) (ValueSome player) "profile kept"
        let after = hide pick player book
        Expect.notEqual after before "a new pseudonym"

    testCase "marks shown with the real profile keep the pseudonym and leave the real names in use" <| fun _ ->
        let book = PseudonymBook.create (dictionary [ "Бард"; "Страж" ])
        let player = profile 5UL "user5" "Страж"
        let mutable next = 0
        let pick _ = next <- next + 1; next - 1
        let hidden = hide pick player book
        let narrowed = PseudonymBook.apply pick HiddenIdentity.ExceptGroundMarks player book
        Expect.equal narrowed (ValueSome hidden) "only the marks changed: the same pseudonym"
        // Its real display name is still shown on its marks, so a newcomer's pick is numbered.
        let other = PseudonymBook.apply (fun _ -> 1) HiddenIdentity.Everywhere (profile 6UL "user6" "B") book
        Expect.equal (other |> ValueOption.map Pseudonym.value) (ValueSome "Страж 2") "real names on marks count as shown"
        Expect.equal (PseudonymBook.apply pick HiddenIdentity.Shown player book) ValueNone "shown drops the pseudonym"
]

let private identityTests = testList "PublicIdentity" [
    testCase "a pseudonymous snapshot keeps the PlayerId and game state but no real name" <| fun _ ->
        let player =
            Player.create (profile 7UL "nerevar" "Nerevar")
            |> Player.beginCharacter (CharacterName.create 128 "Indoril" |> ok)
        let hidden = PlayerSnapshot.withPseudonym (pseudonym "Страж") (Player.snapshot player)
        Expect.equal hidden.Identity (PublicIdentity.Pseudonymous(player.Data.PlayerId, pseudonym "Страж")) "pseudonym stands for the profile"
        Expect.equal hidden.Identity.PlayerId player.Data.PlayerId "PlayerId stays public"
        Expect.equal hidden.CharacterName ValueNone "no character name"
        Expect.isFalse hidden.CharacterNameWithheld "withheld keeps its moderation meaning"
        Expect.equal hidden.CharacterGeneration player.CharacterGeneration "game state stays"

    testCase "presence and chat always hide together; marks may keep the real profile" <| fun _ ->
        Expect.isFalse (HiddenIdentity.isHidden HiddenIdentity.Shown) "shown"
        Expect.isTrue (HiddenIdentity.isHidden HiddenIdentity.ExceptGroundMarks) "hidden in presence and chat"
        Expect.isTrue (HiddenIdentity.coversGroundMarks HiddenIdentity.Everywhere) "marks too"
        Expect.isFalse (HiddenIdentity.coversGroundMarks HiddenIdentity.ExceptGroundMarks) "marks keep the profile"

    testCase "a message and a mark keep the identity of the moment they were made" <| fun _ ->
        let author = profile 7UL "nerevar" "Nerevar"
        let hidden = PublicIdentity.ofProfile (ValueSome (pseudonym "Страж")) author
        let text = ChatMessageText.create 64 "hello" |> ok
        let message = ChatMessage.create (ChatMessageId.create 1UL |> ok) (ChatChannelId.create 1UL |> ok) hidden ValueNone text DateTimeOffset.UnixEpoch
        Expect.equal message.Author (ValueSome hidden) "snapshot at sending"
        let placement =
            GroundMarkPlacement.create (FormKey.create (PluginName.create 64 "Skyrim.esm" |> ok) (LocalFormId.create 60u |> ok))
                Position.zero (Radian.create 0.0f |> ok)
        let mark = GroundMark.create (GroundMarkId.create 1UL |> ok) author.PlayerId (GroundMarkBody.Note (GroundNoteText.create 64 "x" |> ok)) placement DateTimeOffset.UnixEpoch
        Expect.equal (GroundMark.authorIdentity author mark) (PublicIdentity.Profile author) "shown marks follow the profile"
        let pseudonymous = GroundMark.withPseudonym (ValueSome (pseudonym "Страж")) mark
        let renamed = PlayerData.withDisplayName (DisplayName.create 64 "Renamed" |> ok) author
        Expect.equal (GroundMark.authorIdentity renamed pseudonymous) hidden "a hidden mark keeps its pseudonym"
]

let tests = testList "Public identity" [ primitiveTests; dictionaryTests; bookTests; identityTests ]
