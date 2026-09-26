namespace Dreamsleeve.Server.Domain

open System
open System.Text
open FSharp.UMX

/// UMX separates primitive kinds at compile time. Direct UMX.tag calls can bypass
/// validation; use the companion create functions at untrusted input boundaries.
[<AutoOpen>]
module DomainUMX =
    [<Measure>]
    type playerId
    [<Measure>]
    type chatMessageId
    [<Measure>]
    type chatChannelId
    [<Measure>]
    type username
    [<Measure>]
    type displayName
    [<Measure>]
    type characterName
    [<Measure>]
    type chatMessageText
    [<Measure>]
    type chatChannelName
    [<Measure>]
    type locationName
    [<Measure>]
    type worldUnit
    [<Measure>]
    type radian
    [<Measure>]
    type actorValue
    [<Measure>]
    type actorValueName
    [<Measure>]
    type actorValueKey
    [<Measure>]
    type pluginName
    [<Measure>]
    type localFormId

type PluginName = string<pluginName>
type LocalFormId = uint32<localFormId>
type LocationName = string<locationName>
type WorldUnit = float32<worldUnit>
type Radian = float32<radian>
type ActorValueKey = string<actorValueKey>
type ActorValue = float32<actorValue>
type ActorValueName = string<actorValueName>
type PlayerId = uint64<playerId>
type Username = string<username>
type DisplayName = string<displayName>
type CharacterName = string<characterName>
type ChatMessageId = uint64<chatMessageId>
type ChatMessageText = string<chatMessageText>
type ChatChannelId = uint64<chatChannelId>
type ChatChannelName = string<chatChannelName>

[<RequireQualifiedAccess>]
type TextError =
    | Missing
    | TooLong of maximum: int
    | InvalidUnicode
    | InvalidCharacters
    | InvalidFormat

[<RequireQualifiedAccess>]
type DomainError =
    | InvalidText of field: string * error: TextError
    | InvalidLimit of field: string * value: int
    | InvalidId of field: string
    | InvalidLocalFormId of value: uint32
    | NonFiniteNumber of field: string
    | InvalidRadius
    | PlayerIdentityMismatch
    | ChannelMismatch
    | NotChatMember of PlayerId
    | MessageOutOfOrder of lastAccepted: ChatMessageId * received: ChatMessageId

module internal PrimitiveValidation =
    let invalidControl multiline (rune: Rune) =
        if multiline then
            Rune.IsControl rune && rune.Value <> 0x0A && rune.Value <> 0x0D && rune.Value <> 0x09
        else
            Rune.IsControl rune || rune.Value = 0x2028 || rune.Value = 0x2029

    // TryGetRuneAt rejects malformed UTF-16 instead of replacing it with U+FFFD.
    // Validate controls before trimming so a trailing newline cannot disappear.
    let scan multiline (source: string) =
        let mutable offset = 0
        let mutable count = 0
        let mutable error = ValueNone
        let mutable rune = Unchecked.defaultof<Rune>
        while ValueOption.isNone error && offset < source.Length do
            if not (Rune.TryGetRuneAt(source, offset, &rune)) then
                error <- ValueSome TextError.InvalidUnicode
            elif invalidControl multiline rune then
                error <- ValueSome TextError.InvalidCharacters
            else
                offset <- offset + rune.Utf16SequenceLength
                count <- count + 1
        match error with
        | ValueSome error -> Error error
        | ValueNone -> Ok count

    let scalarCount (source: string) =
        let mutable count = 0
        for _ in source.EnumerateRunes() do
            count <- count + 1
        count

    let unrestricted (_: string) = ValueNone

    let private checkedText allowBlank field maxLength transform multiline validate (source: string) =
        let fail error = Error(DomainError.InvalidText(field, error))
        if maxLength <= 0 then
            Error(DomainError.InvalidLimit(field, maxLength))
        elif isNull source || (not allowBlank && String.IsNullOrWhiteSpace source) then
            fail TextError.Missing
        else
            match scan multiline source with
            | Error error -> fail error
            | Ok sourceLength ->
                let transformed =
                    try Ok(transform source)
                    with :? ArgumentException -> Error TextError.InvalidUnicode
                match transformed with
                | Error error -> fail error
                | Ok canonical ->
                    // Trim, NFC and ASCII folding preserve valid Unicode. Text that
                    // remains unchanged needs neither decoding nor counting twice.
                    let length =
                        if Object.ReferenceEquals(source, canonical) then sourceLength
                        else scalarCount canonical
                    if length > maxLength then fail (TextError.TooLong maxLength)
                    else
                        match validate canonical with
                        | ValueSome error -> fail error
                        | ValueNone -> Ok canonical

    /// Limits count Unicode scalar values in the resulting text, not UTF-16 code units.
    let text field maxLength transform multiline validate source =
        checkedText false field maxLength transform multiline validate source

    /// Game labels may be absent and are preserved exactly; they do not identify data.
    let label field maxLength source =
        checkedText true field maxLength id false unrestricted source

    let nfcTrim (source: string) = source.Trim().Normalize(NormalizationForm.FormC)

    let asciiLower (source: string) =
        source
        |> String.map (fun character ->
            if character >= 'A' && character <= 'Z' then
                char (int character + int 'a' - int 'A')
            else
                character)

    let name field maxLength source =
        text field maxLength nfcTrim false unrestricted source

    let asciiLetterOrDigit character =
        (character >= 'a' && character <= 'z')
        || (character >= 'A' && character <= 'Z')
        || (character >= '0' && character <= '9')

    let username (source: string) =
        if source |> Seq.forall (fun c -> asciiLetterOrDigit c || c = '_' || c = '.') then
            ValueNone
        else
            ValueSome TextError.InvalidCharacters

    let actorValueKey (source: string) =
        let separator = source.IndexOf ':'
        if separator <= 0 || separator = source.Length - 1 then
            ValueSome TextError.InvalidFormat
        else
            let prefix = source.Substring(0, separator)
            let name = source.Substring(separator + 1)
            if prefix |> Seq.exists (fun c -> not (asciiLetterOrDigit c || c = '.' || c = '_' || c = '-')) then
                ValueSome TextError.InvalidCharacters
            elif String.IsNullOrWhiteSpace name || Char.IsWhiteSpace name[0] || Char.IsWhiteSpace name[name.Length - 1] then
                ValueSome TextError.InvalidFormat
            else
                ValueNone

    let identifier field (raw: uint64) =
        if raw = 0UL then Error(DomainError.InvalidId field) else Ok raw

    let finite field (raw: float32) =
        if Single.IsFinite raw then Ok raw else Error(DomainError.NonFiniteNumber field)

[<RequireQualifiedAccess>]
module PluginName =
    let value (name: PluginName) : string = UMX.untag name
    /// The game adapter supplies the actual plugin filename; the server treats it as a key.
    /// ASCII case folding only: non-ASCII text is preserved; no Unicode normalization or trimming.
    let create maxLength raw : Result<PluginName, DomainError> =
        PrimitiveValidation.text "PluginName" maxLength PrimitiveValidation.asciiLower
            false PrimitiveValidation.unrestricted raw
        |> Result.map UMX.tag

[<RequireQualifiedAccess>]
module LocalFormId =
    let value (id: LocalFormId) : uint32 = uint32 id
    /// Rejects zero and values outside the 24-bit local range.
    /// The client must correctly extract local IDs for both full and light plugins.
    let create (raw: uint32) : Result<LocalFormId, DomainError> =
        if raw = 0u || raw > 0x00FFFFFFu then
            Error(DomainError.InvalidLocalFormId raw)
        else
            Ok(LanguagePrimitives.UInt32WithMeasure<localFormId> raw)

[<RequireQualifiedAccess>]
module LocationName =
    let value (name: LocationName) : string = UMX.untag name
    /// Preserves the game's display label, including an empty label or surrounding spaces.
    let create maxLength raw : Result<LocationName, DomainError> =
        PrimitiveValidation.label "LocationName" maxLength raw |> Result.map UMX.tag

[<RequireQualifiedAccess>]
module WorldUnit =
    let value (amount: WorldUnit) : float32 = UMX.untag amount
    /// Native game units are preserved; only NaN and infinities are rejected.
    let create raw : Result<WorldUnit, DomainError> =
        PrimitiveValidation.finite "WorldUnit" raw |> Result.map UMX.tag

[<RequireQualifiedAccess>]
module Radian =
    let value (amount: Radian) : float32 = UMX.untag amount
    /// Native radians are preserved without angle wrapping.
    let create raw : Result<Radian, DomainError> =
        PrimitiveValidation.finite "Radian" raw |> Result.map UMX.tag

[<RequireQualifiedAccess>]
module ActorValueKey =
    let value (key: ActorValueKey) : string = UMX.untag key
    /// Namespace:name. The namespace is ASCII; the name may be Unicode.
    /// ASCII case folding only: non-ASCII text is preserved; no Unicode normalization or trimming.
    /// The first colon separates the namespace from the mod's registered machine name.
    let create maxLength raw : Result<ActorValueKey, DomainError> =
        PrimitiveValidation.text "ActorValueKey" maxLength PrimitiveValidation.asciiLower
            false PrimitiveValidation.actorValueKey raw
        |> Result.map UMX.tag

[<RequireQualifiedAccess>]
module ActorValue =
    let value (amount: ActorValue) : float32 = UMX.untag amount
    /// Values are relayed as supplied; negative values are allowed, NaN/infinities are not.
    let create raw : Result<ActorValue, DomainError> =
        PrimitiveValidation.finite "ActorValue" raw |> Result.map UMX.tag

[<RequireQualifiedAccess>]
module ActorValueName =
    let value (name: ActorValueName) : string = UMX.untag name
    /// Preserves the game's display label, including an empty label or surrounding spaces.
    let create maxLength raw : Result<ActorValueName, DomainError> =
        PrimitiveValidation.label "ActorValueName" maxLength raw |> Result.map UMX.tag

[<RequireQualifiedAccess>]
module PlayerId =
    let value (id: PlayerId) : uint64 = UMX.untag id
    let create raw : Result<PlayerId, DomainError> =
        PrimitiveValidation.identifier "PlayerId" raw |> Result.map UMX.tag

[<RequireQualifiedAccess>]
module Username =
    let value (name: Username) : string = UMX.untag name
    /// Trims, accepts only ASCII letters/digits/underscore/dot, then lowercases.
    /// Availability and uniqueness are checked by the owning server agent.
    let create maxLength raw : Result<Username, DomainError> =
        PrimitiveValidation.text "Username" maxLength (fun source -> source.Trim())
            false PrimitiveValidation.username raw
        |> Result.map (fun canonical -> UMX.tag(canonical.ToLowerInvariant()))

[<RequireQualifiedAccess>]
module DisplayName =
    let value (name: DisplayName) : string = UMX.untag name
    /// Trims and normalizes to NFC while preserving case; duplicates are allowed.
    let create maxLength raw : Result<DisplayName, DomainError> =
        PrimitiveValidation.name "DisplayName" maxLength raw |> Result.map UMX.tag

[<RequireQualifiedAccess>]
module CharacterName =
    let value (name: CharacterName) : string = UMX.untag name
    /// Preserves the game's original text, including case and surrounding spaces.
    let create maxLength raw : Result<CharacterName, DomainError> =
        PrimitiveValidation.text "CharacterName" maxLength id false PrimitiveValidation.unrestricted raw
        |> Result.map UMX.tag

[<RequireQualifiedAccess>]
module ChatMessageId =
    let value (id: ChatMessageId) : uint64 = UMX.untag id
    let create raw : Result<ChatMessageId, DomainError> =
        PrimitiveValidation.identifier "ChatMessageId" raw |> Result.map UMX.tag

[<RequireQualifiedAccess>]
module ChatMessageText =
    let value (message: ChatMessageText) : string = UMX.untag message
    /// Preserves original text. Newlines and tabs are allowed; other controls are not.
    let create maxLength raw : Result<ChatMessageText, DomainError> =
        PrimitiveValidation.text "ChatMessageText" maxLength id true PrimitiveValidation.unrestricted raw
        |> Result.map UMX.tag

[<RequireQualifiedAccess>]
module ChatChannelId =
    let value (id: ChatChannelId) : uint64 = UMX.untag id
    let create raw : Result<ChatChannelId, DomainError> =
        PrimitiveValidation.identifier "ChatChannelId" raw |> Result.map UMX.tag

[<RequireQualifiedAccess>]
module ChatChannelName =
    let value (name: ChatChannelName) : string = UMX.untag name
    let create maxLength raw : Result<ChatChannelName, DomainError> =
        PrimitiveValidation.name "ChatChannelName" maxLength raw |> Result.map UMX.tag
