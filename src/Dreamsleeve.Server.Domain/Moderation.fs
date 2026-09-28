namespace Dreamsleeve.Server.Domain

open System
open System.Globalization
open System.Text
open System.Text.RegularExpressions
open FSharp.UMX

/// A server word list. Matching works on a separate normalized projection;
/// accepted text is stored and relayed exactly as the author sent it.
/// This is a baseline filter, not complete moderation.
[<NoEquality; NoComparison>]
type ModerationRules =
    private {
        rules: (string * Regex) array
        exceptions: Regex array
    }

    member this.IsEmpty = this.rules.Length = 0

/// A rule that matched outside every exception. Pattern is the configured
/// rule text for server diagnostics; never echo it back to other players.
type ModerationMatch = { Pattern: string }

/// Configured lists: whole words or phrases, explicit substrings and allowed
/// words that would otherwise contain a match.
type ModerationSource = {
    Words: string list
    Substrings: string list
    Exceptions: string list
}

[<RequireQualifiedAccess>]
module Moderation =
    // Common look-alikes after lower-casing: Cyrillic and Greek letters that
    // render like Latin ones. Rules pass through the same projection, so a
    // Russian rule and Russian text still meet; only the letter identity is shared.
    let private homoglyphs =
        dict [
            'а', 'a'; 'в', 'b'; 'е', 'e'; 'к', 'k'; 'м', 'm'; 'н', 'h'; 'о', 'o'; 'р', 'p'
            'с', 'c'; 'т', 't'; 'у', 'y'; 'х', 'x'; 'і', 'i'; 'ј', 'j'; 'ѕ', 's'; 'ԁ', 'd'
            'α', 'a'; 'β', 'b'; 'ε', 'e'; 'ι', 'i'; 'κ', 'k'; 'ν', 'v'; 'ο', 'o'; 'ρ', 'p'
            'τ', 't'; 'υ', 'u'; 'χ', 'x'; 'ı', 'i'
        ]

    // Digits and symbols commonly typed instead of a letter. Each maps to one
    // letter only, so the classes of different rule letters never overlap.
    let private substitutes =
        dict [
            'a', "4@"; 'b', "8"; 'e', "3"; 'i', "1!|"; 'o', "0"; 's', "5$"; 't', "7"
        ]

    // Invisible letters and fillers that are not in the Cf category.
    let private invisible (rune: Rune) =
        match rune.Value with
        | 0x115F | 0x1160 | 0x3164 | 0xFFA0 | 0x2800 | 0x180E -> true
        | _ -> false

    /// Matching projection: NFKC, invariant lower case, removed marks, format
    /// and control characters, folded look-alike letters and collapsed spaces.
    /// It is never shown or stored instead of the original text.
    let normalize (source: string) =
        if String.IsNullOrEmpty source then ""
        else
            let decomposed =
                try source.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Normalize(NormalizationForm.FormD)
                with :? ArgumentException -> source.ToLowerInvariant()
            let builder = StringBuilder(decomposed.Length)
            let mutable space = false
            for rune in decomposed.EnumerateRunes() do
                match Rune.GetUnicodeCategory rune with
                | UnicodeCategory.NonSpacingMark | UnicodeCategory.EnclosingMark | UnicodeCategory.Format -> ()
                | _ when invisible rune -> ()
                | _ when Rune.IsWhiteSpace rune || Rune.IsControl rune ->
                    // Line breaks and tabs separate words like spaces do.
                    space <- builder.Length > 0
                | _ ->
                    if space then builder.Append ' ' |> ignore
                    space <- false
                    if rune.IsBmp && homoglyphs.ContainsKey(char rune.Value) then
                        builder.Append homoglyphs[char rune.Value] |> ignore
                    else
                        builder.Append(rune.ToString()) |> ignore
            builder.ToString()

    // Separators that may be typed inside a word ("b.a.d", "b-a-d"). They
    // exclude every letter substitute, keeping each step of a match deterministic.
    let private separator = @"[^\p{L}\p{N}\s@$!|]"
    let private boundaryBefore = @"(?<![\p{L}\p{N}])"
    let private boundaryAfter = @"(?![\p{L}\p{N}])"

    let private letterClass (letter: char) =
        match substitutes.TryGetValue letter with
        | true, extra -> "[" + Regex.Escape(string letter) + Regex.Escape extra + "]"
        | false, _ -> Regex.Escape(string letter)

    // Runs of one letter must repeat at least as often as in the rule, so a
    // stretched word still matches while "as" never matches the rule "ass".
    // Atomic groups keep matching linear: neighbouring classes are disjoint.
    let private pattern wholeWord (rule: string) =
        let normalized = normalize rule
        if normalized.Length = 0 then None
        else
            let parts = ResizeArray<string>()
            let mutable index = 0
            while index < normalized.Length do
                let current = normalized[index]
                let mutable count = 1
                while index + count < normalized.Length && normalized[index + count] = current do
                    count <- count + 1
                if current = ' ' then
                    parts.Add(@"(?>(?:\s|" + separator + ")+)")
                else
                    if parts.Count > 0 && normalized[index - 1] <> ' ' then parts.Add("(?>" + separator + "*)")
                    parts.Add($"(?>{letterClass current}{{{count},}})")
                index <- index + count
            let body = String.concat "" parts
            Some(if wholeWord then boundaryBefore + body + boundaryAfter else body)

    let private compile wholeWord rule =
        pattern wholeWord rule
        |> Option.map (fun text -> Regex(text, RegexOptions.CultureInvariant ||| RegexOptions.Compiled))

    let empty = { rules = Array.empty; exceptions = Array.empty }

    /// Blank entries are ignored. Exceptions are whole words or phrases.
    let create (source: ModerationSource) : ModerationRules =
        let entries wholeWord values =
            values
            |> List.distinct
            |> List.choose (fun rule -> compile wholeWord rule |> Option.map (fun regex -> rule, regex))
        {
            rules = Array.ofList (entries true source.Words @ entries false source.Substrings)
            exceptions = source.Exceptions |> List.distinct |> List.choose (compile true) |> Array.ofList
        }

    let private allowedSpans (rules: ModerationRules) (text: string) =
        [| for allowed in rules.exceptions do
               for found in allowed.Matches text -> struct (found.Index, found.Index + found.Length) |]

    /// Finds the first rule match not contained in an exception occurrence.
    let check (rules: ModerationRules) (text: string) : ModerationMatch voption =
        if rules.rules.Length = 0 || String.IsNullOrEmpty text then ValueNone
        else
            let normalized = normalize text
            let spans = lazy (allowedSpans rules normalized)
            let excepted (found: Match) =
                spans.Value |> Array.exists (fun struct (start, finish) -> found.Index >= start && found.Index + found.Length <= finish)
            let rec search (regex: Regex) start =
                if start > normalized.Length then false
                else
                    let found = regex.Match(normalized, start)
                    if not found.Success then false
                    elif excepted found then search regex (found.Index + 1)
                    else true
            rules.rules
            |> Array.tryFind (fun (_, regex) -> search regex 0)
            |> function
                | Some (rule, _) -> ValueSome { Pattern = rule }
                | None -> ValueNone

    let allows rules text = (check rules text).IsNone

    /// Placeholder for a stored display name that fails the current rules.
    let fallbackDisplayName (playerId: PlayerId) : DisplayName =
        UMX.tag $"Player {PlayerId.value playerId}"

    [<Literal>]
    let HiddenUsernamePrefix = "hidden."

    /// Placeholder for a stored username that fails the current rules. The
    /// prefix is reserved at registration, so it cannot impersonate an account.
    let fallbackUsername (playerId: PlayerId) : Username =
        UMX.tag $"{HiddenUsernamePrefix}{PlayerId.value playerId}"

    /// Stored profiles are never rewritten. Outbound copies hide names that
    /// fail the current rules; IDs and the stored account stay unchanged.
    let publicProfile rules (profile: PlayerData) =
        let username =
            if allows rules (Username.value profile.Username) then profile.Username
            else fallbackUsername profile.PlayerId
        let displayName =
            if allows rules (DisplayName.value profile.DisplayName) then profile.DisplayName
            else fallbackDisplayName profile.PlayerId
        if username = profile.Username && displayName = profile.DisplayName then profile
        else PlayerData.create profile.PlayerId username displayName
