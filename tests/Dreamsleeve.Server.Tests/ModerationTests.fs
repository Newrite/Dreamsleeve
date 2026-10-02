module Dreamsleeve.Server.Tests.ModerationTests

open Expecto
open Dreamsleeve.Server.Domain

let private rules =
    Moderation.create {
        Words = ["badword"; "ass"; "bad phrase"; "хуй"]
        Substrings = ["cunt"]
        Exceptions = ["Scunthorpe"; "badword ministry"]
    }

let private blocked text = Expect.isFalse (Moderation.allows rules text) $"Expected a match: {text}"
let private allowed text = Expect.isTrue (Moderation.allows rules text) $"Expected no match: {text}"

let tests = testList "Moderation" [
    test "normalization folds case, width, marks, invisible characters and look-alikes" {
        Expect.equal (Moderation.normalize "ＢＡＤ") "bad" "fullwidth and case"
        Expect.equal (Moderation.normalize "Bád\u200Bword") "badword" "accent and zero-width space"
        Expect.equal (Moderation.normalize "  two\n\tlines  ") "two lines" "whitespace collapses"
        Expect.equal (Moderation.normalize "Вас") "bac" "Cyrillic look-alikes fold to Latin"
        Expect.equal (Moderation.normalize "\u3164x\u00ADy") "xy" "fillers and soft hyphen are removed"
    }

    test "whole words need boundaries, substitutes and stretched letters still match" {
        blocked "you badword"
        blocked "BADWORD!"
        blocked "b@dw0rd"
        blocked "baaadwoooord"
        blocked "b.a.d.w.o.r.d"
        blocked "bаdwоrd"            // Cyrillic а and о
        blocked "bad\u200Dword"
        blocked "a$$"
        blocked "bad    phrase"
        blocked "bad.phrase"
        allowed "badwords"            // Different word: boundary after.
        allowed "class assignment"    // Word rule never matches inside a word.
        allowed "as"                  // Fewer repeated letters than the rule.
        allowed "bad luck with a phrase"
    }

    test "substrings are explicit and exceptions cover only their own span" {
        blocked "xcuntx"
        allowed "Scunthorpe United"
        blocked "Scunthorpe cunt"
        allowed "the badword ministry"
        blocked "the badword ministry of badword"
    }

    test "international names without rule words stay allowed" {
        for name in ["Zoë"; "Łukasz"; "Олег"; "Ἀθηνᾶ"; "李小龙"; "محمد"; "Ingrid Nordström"; "Dovahkiin"] do
            allowed name
        blocked "Хуй"
        blocked "х.у.й"
        allowed "х у й"              // Spaced single letters are not joined.
        allowed "хуйство"            // Word rule, not a substring rule.
    }

    test "empty rules allow everything and blank rules are ignored" {
        let empty = Moderation.create { Words = [""; "   "]; Substrings = []; Exceptions = [] }
        Expect.isTrue empty.IsEmpty "Blank entries compile to nothing"
        Expect.isTrue (Moderation.allows Moderation.empty "badword") "Disabled moderation allows text"
    }

    test "flag tier marks UTF-8 byte ranges of the original text without refusing it" {
        let flags =
            Moderation.create { Words = []; Substrings = []; Exceptions = [] }
            |> Moderation.withFlags { Words = ["badword"; "плохо"]; Substrings = ["zzz"]; Exceptions = ["zzzok"] }
        Expect.isTrue (Moderation.allows flags "badword") "flag rules never refuse"
        Expect.isTrue flags.HasFlags "flag tier present"
        let text = "Ой, B\u200Ba\u0301dword и ПЛОХО!"
        let spans = Moderation.flag flags text
        let bytes = System.Text.Encoding.UTF8.GetBytes text
        let cut (span: TextSpan) = System.Text.Encoding.UTF8.GetString(bytes, span.Start, span.Length)
        Expect.equal (spans |> List.map cut) ["B\u200Ba\u0301dword"; "ПЛОХО"] "original text including invisible and combining characters"
        Expect.equal (Moderation.flag flags "xzzzzx zzzok") [{ Start = 1; Length = 4 }] "stretched substring; exception kept"
        Expect.equal (Moderation.flag flags "zzzbadword").Length 1 "overlapping and adjacent ranges merge once"
        Expect.isEmpty (Moderation.flag rules "badword") "no flag tier, no ranges"
    }

    test "public profiles hide failing stored names without changing identity" {
        let id = PlayerId.create 9UL |> Result.defaultWith (failwithf "%A")
        let stored = PlayerData.create id (Username.create 32 "ass" |> Result.defaultWith (failwithf "%A"))
                         (DisplayName.create 64 "Sir Badword" |> Result.defaultWith (failwithf "%A")) NameColor.unknown
        let shown = Moderation.publicProfile rules stored
        Expect.equal shown.PlayerId id "same account"
        Expect.equal (Username.value shown.Username) "hidden.9" "username placeholder"
        Expect.equal (DisplayName.value shown.DisplayName) "Player 9" "display placeholder"
        let clean = PlayerData.create id (Username.create 32 "lydia" |> Result.defaultWith (failwithf "%A"))
                        (DisplayName.create 64 "Lydia" |> Result.defaultWith (failwithf "%A")) NameColor.unknown
        Expect.equal (Moderation.publicProfile rules clean) clean "allowed profile is unchanged"
    }
]
