namespace Dreamsleeve.Server.Domain

open System.Collections.Generic
open System.Text
open FSharp.UMX

/// The server dictionary of pseudonyms (pseudonyms.toml): distinct valid
/// entries in their file order, never empty.
type PseudonymDictionary =
    private { names: Pseudonym array }

    member this.Names = List.ofArray this.names
    member this.Count = this.names.Length

[<RequireQualifiedAccess>]
module PseudonymDictionary =
    /// More entries are ignored, like the client's alias dictionary.
    [<Literal>]
    let MaxEntries = 1024

    /// Names are compared online without case, after NFC.
    let key (name: string) = name.Normalize(NormalizationForm.FormC).ToLowerInvariant()

    /// Entries already accepted by Pseudonym.create; repeats are dropped.
    /// ValueNone when nothing is left: the caller falls back to builtIn.
    let create (entries: Pseudonym seq) : PseudonymDictionary voption =
        let seen = HashSet<string>()
        let names =
            entries
            |> Seq.filter (fun name -> seen.Add(key (Pseudonym.value name)))
            |> Seq.truncate MaxEntries
            |> Array.ofSeq
        if names.Length = 0 then ValueNone else ValueSome { names = names }

    /// The 24 names of the client's built-in aliases.toml, in the same order.
    let builtIn =
        { names =
            [| "Странник"; "Следопыт"; "Бард"; "Изгнанник"; "Наёмник"; "Паломник"
               "Охотник"; "Кузнец"; "Травник"; "Мореход"; "Караванщик"; "Лучник"
               "Страж"; "Скиталец"; "Отшельник"; "Алхимик"; "Чародей"; "Воитель"
               "Рудокоп"; "Лесоруб"; "Рыбак"; "Менестрель"; "Ловчий"; "Книжник" |]
            |> Array.map UMX.tag<pseudonym> }

/// The names each online player is shown under and the pseudonyms of those
/// who hide theirs. One owner keeps it; a pseudonym lives as long as the
/// session that received it and is never stored.
[<NoEquality; NoComparison>]
type PseudonymBook =
    private {
        dictionary: PseudonymDictionary
        players: Dictionary<PlayerId, struct (PlayerData * Pseudonym voption * HiddenIdentity)>
    }

    member this.Dictionary = this.dictionary
    member this.Count = this.players.Count

[<RequireQualifiedAccess>]
module PseudonymBook =
    let create dictionary : PseudonymBook = { dictionary = dictionary; players = Dictionary() }

    let private realNames (profile: PlayerData) =
        [ Username.value profile.Username; DisplayName.value profile.DisplayName ]

    // Real names stay visible on ground marks of a player who hides them elsewhere.
    let private shownNames struct (profile: PlayerData, pseudonym: Pseudonym voption, hiding: HiddenIdentity) =
        match pseudonym, hiding with
        | ValueSome name, HiddenIdentity.ExceptGroundMarks -> Pseudonym.value name :: realNames profile
        | ValueSome name, (HiddenIdentity.Everywhere | HiddenIdentity.Shown) -> [ Pseudonym.value name ]
        | ValueNone, _ -> realNames profile

    /// Registers the profile as shown everywhere, dropping any pseudonym of the player.
    let show (profile: PlayerData) (book: PseudonymBook) =
        book.players[profile.PlayerId] <- struct (profile, ValueNone, HiddenIdentity.Shown)

    // A dictionary entry chosen by pick (an index below the dictionary size),
    // never from the player's names. A name already shown online, the player's
    // own real names included, gets the lowest free number: "Страж 2".
    let private choose (pick: int -> int) (profile: PlayerData) (book: PseudonymBook) : Pseudonym =
        let names = book.dictionary.names
        let chosen = names[(pick names.Length % names.Length + names.Length) % names.Length]
        let used = HashSet<string>(realNames profile |> List.map PseudonymDictionary.key)
        for entry in book.players do
            if entry.Key <> profile.PlayerId then
                for name in shownNames entry.Value do used.Add(PseudonymDictionary.key name) |> ignore
        let mutable number = 1
        while used.Contains(PseudonymDictionary.key (Pseudonym.value (Pseudonym.numbered number chosen))) do
            number <- number + 1
        Pseudonym.numbered number chosen

    /// Hides the player's names as chosen. A player who showed them everywhere
    /// gets a new pseudonym; one already hidden keeps theirs when only the
    /// ground marks change. Shown drops the pseudonym, so the next hiding picks anew.
    let apply (pick: int -> int) (hiding: HiddenIdentity) (profile: PlayerData) (book: PseudonymBook) : Pseudonym voption =
        match hiding with
        | HiddenIdentity.Shown ->
            show profile book
            ValueNone
        | HiddenIdentity.Everywhere | HiddenIdentity.ExceptGroundMarks ->
            let current =
                match book.players.TryGetValue profile.PlayerId with
                | true, struct (_, ValueSome pseudonym, _) -> pseudonym
                | true, struct (_, ValueNone, _) | false, _ -> choose pick profile book
            book.players[profile.PlayerId] <- struct (profile, ValueSome current, hiding)
            ValueSome current

    /// The profile the online player was registered with.
    let tryProfile playerId (book: PseudonymBook) =
        match book.players.TryGetValue playerId with
        | true, struct (profile, _, _) -> ValueSome profile
        | false, _ -> ValueNone

    let tryFind playerId (book: PseudonymBook) =
        match book.players.TryGetValue playerId with
        | true, struct (_, pseudonym, _) -> pseudonym
        | false, _ -> ValueNone

    /// The player is no longer online; its names and pseudonym are free.
    let release playerId (book: PseudonymBook) =
        book.players.Remove playerId |> ignore
