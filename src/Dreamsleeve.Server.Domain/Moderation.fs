namespace Dreamsleeve.Server.Domain

open System
open System.Globalization
open System.Text
open System.Text.RegularExpressions
open FSharp.UMX

/// One list of compiled rules with its own exceptions.
[<NoEquality; NoComparison>]
type internal ModerationTier = {
    Rules: (string * Regex) array
    Exceptions: Regex array
}

/// A server word list in two tiers. Block rules refuse names and messages;
/// flag rules only mark ranges of accepted messages for clients to show, mask
/// or hide. Matching works on a separate normalized projection; accepted text
/// is stored and relayed exactly as the author sent it. This is a baseline
/// filter, not complete moderation.
[<NoEquality; NoComparison>]
type ModerationRules =
    private {
        block: ModerationTier
        flag: ModerationTier
    }

    member this.IsEmpty = this.block.Rules.Length = 0
    member this.HasFlags = this.flag.Rules.Length > 0

/// A rule that matched outside every exception. Pattern is the configured
/// rule text for server diagnostics; never echo it back to other players.
type ModerationMatch = { Pattern: string }

/// Configured lists of one tier: whole words or phrases, explicit substrings
/// and allowed words that would otherwise contain a match.
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

    /// Normalized text with, for every character, the UTF-16 range of the
    /// source text it came from.
    [<NoEquality; NoComparison>]
    type private Projection = { Text: string; Starts: int array; Ends: int array }

    // Folded rune by rune so every output character keeps its source range.
    // Marks are removed after decomposition, so per-rune NFKC gives the same
    // letters as normalizing the whole string.
    let private project (source: string) =
        if String.IsNullOrEmpty source then { Text = ""; Starts = Array.empty; Ends = Array.empty }
        else
            let builder = StringBuilder(source.Length)
            let starts = ResizeArray<int>(source.Length)
            let ends = ResizeArray<int>(source.Length)
            let mutable spaceStart = -1
            let mutable spaceEnd = -1
            let append (text: string) first last =
                // Line breaks and tabs separate words like spaces do; leading
                // and trailing whitespace is dropped, runs collapse to one space.
                if spaceStart >= 0 then
                    if builder.Length > 0 then
                        builder.Append ' ' |> ignore
                        starts.Add spaceStart
                        ends.Add spaceEnd
                    spaceStart <- -1
                for character in text do
                    builder.Append character |> ignore
                    starts.Add first
                    ends.Add last
            let mutable offset = 0
            while offset < source.Length do
                let mutable rune = Unchecked.defaultof<Rune>
                let length =
                    if Rune.TryGetRuneAt(source, offset, &rune) then rune.Utf16SequenceLength
                    else
                        rune <- Rune.ReplacementChar
                        1
                let first, last = offset, offset + length
                let folded =
                    if rune.IsAscii then (Rune.ToLowerInvariant rune).ToString()
                    else
                        try rune.ToString().Normalize(NormalizationForm.FormKC).ToLowerInvariant().Normalize(NormalizationForm.FormD)
                        with :? ArgumentException -> rune.ToString().ToLowerInvariant()
                for part in folded.EnumerateRunes() do
                    match Rune.GetUnicodeCategory part with
                    | UnicodeCategory.NonSpacingMark | UnicodeCategory.EnclosingMark | UnicodeCategory.Format -> ()
                    | _ when invisible part -> ()
                    | _ when Rune.IsWhiteSpace part || Rune.IsControl part ->
                        if spaceStart < 0 then spaceStart <- first
                        spaceEnd <- last
                    | _ when part.IsBmp && homoglyphs.ContainsKey(char part.Value) ->
                        append (string homoglyphs[char part.Value]) first last
                    | _ -> append (part.ToString()) first last
                offset <- last
            { Text = builder.ToString(); Starts = starts.ToArray(); Ends = ends.ToArray() }

    /// Matching projection: NFKC, invariant lower case, removed marks, format
    /// and control characters, folded look-alike letters and collapsed spaces.
    /// It is never shown or stored instead of the original text.
    let normalize (source: string) = (project source).Text

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
    // Punctuation inside a rule ("t.me", "14/88") is optional like any typed
    // separator. Atomic groups keep matching linear: neighbouring classes are disjoint.
    let private pattern wholeWord (rule: string) =
        let normalized =
            normalize rule
            |> String.filter (fun c -> c = ' ' || Char.IsLetterOrDigit c || Char.IsSurrogate c)
        let normalized = normalized.Trim()
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

    let private tier (source: ModerationSource) : ModerationTier =
        let entries wholeWord values =
            values
            |> List.distinct
            |> List.choose (fun rule -> compile wholeWord rule |> Option.map (fun regex -> rule, regex))
        {
            Rules = Array.ofList (entries true source.Words @ entries false source.Substrings)
            Exceptions = source.Exceptions |> List.distinct |> List.choose (compile true) |> Array.ofList
        }

    let private none = { Rules = Array.empty; Exceptions = Array.empty }

    let empty = { block = none; flag = none }

    /// Block tier. Blank entries are ignored. Exceptions are whole words or phrases.
    let create (source: ModerationSource) : ModerationRules = { empty with block = tier source }

    /// Adds the flag tier: matches mark accepted messages instead of refusing them.
    let withFlags (source: ModerationSource) (rules: ModerationRules) = { rules with flag = tier source }

    // Every rule match that does not lie entirely inside an exception
    // occurrence, as [start, finish) indices of the normalized text.
    let private matches (tier: ModerationTier) firstOnly (text: string) =
        let allowed =
            lazy [| for allowedRule in tier.Exceptions do
                        for found in allowedRule.Matches text -> struct (found.Index, found.Index + found.Length) |]
        let excepted (found: Match) =
            allowed.Value |> Array.exists (fun struct (start, finish) -> found.Index >= start && found.Index + found.Length <= finish)
        let result = ResizeArray<string * int * int>()
        let mutable ruleIndex = 0
        while ruleIndex < tier.Rules.Length && not (firstOnly && result.Count > 0) do
            let rule, regex = tier.Rules[ruleIndex]
            let mutable found = regex.Match(text, 0)
            while found.Success && not (firstOnly && result.Count > 0) do
                // Lookbehind still sees the text before startat, so boundaries hold.
                if excepted found then found <- regex.Match(text, found.Index + 1)
                else
                    result.Add((rule, found.Index, found.Index + found.Length))
                    found <- regex.Match(text, found.Index + found.Length)
            ruleIndex <- ruleIndex + 1
        result

    /// Finds the first block rule match not contained in an exception occurrence.
    let check (rules: ModerationRules) (text: string) : ModerationMatch voption =
        if rules.block.Rules.Length = 0 || String.IsNullOrEmpty text then ValueNone
        else
            let found = matches rules.block true (normalize text)
            if found.Count = 0 then ValueNone
            else
                let rule, _, _ = found[0]
                ValueSome { Pattern = rule }

    let allows rules text = (check rules text).IsNone

    /// Flag tier ranges of the original text in UTF-8 bytes: merged, ascending,
    /// on code point boundaries. Empty when nothing matched.
    let flag (rules: ModerationRules) (text: string) : TextSpan list =
        if rules.flag.Rules.Length = 0 || String.IsNullOrEmpty text then []
        else
            let projection = project text
            let ranges =
                matches rules.flag false projection.Text
                |> Seq.map (fun (_, start, finish) -> projection.Starts[start], projection.Ends[finish - 1])
                |> Seq.sortBy fst
                |> Seq.fold (fun merged (start, finish) ->
                    match merged with
                    | (previousStart, previousFinish) :: rest when start <= previousFinish ->
                        (previousStart, max previousFinish finish) :: rest
                    | _ -> (start, finish) :: merged) []
                |> List.rev
            let bytes (index: int) = Encoding.UTF8.GetByteCount(text.AsSpan(0, index))
            ranges |> List.map (fun (start, finish) ->
                let first = bytes start
                { Start = first; Length = bytes finish - first })

    /// Placeholder for a stored display name that fails the current rules.
    let fallbackDisplayName (playerId: PlayerId) : DisplayName =
        UMX.tag $"Player {PlayerId.value playerId}"

    [<Literal>]
    let HiddenUsernamePrefix = "hidden."

    /// Placeholder for a stored username that fails the current rules. The
    /// prefix is reserved at registration, so it cannot impersonate an account.
    let fallbackUsername (playerId: PlayerId) : Username =
        UMX.tag $"{HiddenUsernamePrefix}{PlayerId.value playerId}"

    /// Names a new account cannot take: placeholders and the names of the
    /// system source, so no player passes for the server in any client.
    let reservedUsername (username: Username) =
        let text = Username.value username
        text.StartsWith HiddenUsernamePrefix || text = "server" || text = "system"

    /// A name a new account may take: not reserved and allowed by the block
    /// rules. Registration and accounts created in the panel check it; sign-in never does.
    let allowsUsername rules (username: Username) =
        not (reservedUsername username) && allows rules (Username.value username)

    /// Stored profiles are never rewritten. Outbound copies hide names that
    /// fail the current block rules; IDs and the stored account stay unchanged.
    let publicProfile rules (profile: PlayerData) =
        let username =
            if allows rules (Username.value profile.Username) then profile.Username
            else fallbackUsername profile.PlayerId
        let displayName =
            if allows rules (DisplayName.value profile.DisplayName) then profile.DisplayName
            else fallbackDisplayName profile.PlayerId
        if username = profile.Username && displayName = profile.DisplayName then profile
        else PlayerData.create profile.PlayerId username displayName
