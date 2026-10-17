namespace Dreamsleeve.Server

open System
open System.IO
open System.Net
open System.Globalization
open System.Security
open Microsoft.FSharp.Reflection
open Tomlyn
open Tomlyn.Model
open Dreamsleeve.Agent
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Infrastructure

/// An HTTP(S) listener of the server: authentication or the admin panel.
type HttpListenerSettings = {
    ListenUrl: string
    AllowInsecureLoopback: bool
    /// Passwords without TLS outside loopback only by explicit choice.
    AllowInsecureRemote: bool
    /// Direct HTTPS without a reverse proxy.
    CertificatePath: string
    /// X-Forwarded-For/Proto from a proxy on this machine only.
    TrustForwardedHeaders: bool
    /// Requests per client address and minute; the panel's sign-in has its own limit.
    RequestsPerMinute: int
    RequestTimeoutSeconds: int
}

/// "Sign in through Steam" (docs/AuthenticationRu.md, «Вход через Steam»). The
/// Web API key is not a setting: the optional DREAMSLEEVE_STEAM_WEB_API_KEY
/// environment variable, so it never shows on the panel's configuration page.
type SteamSettings = {
    Enabled: bool
    /// The origin of the authentication host as players' browsers reach it,
    /// "https://auth.example.org"; Steam sends the browser back there.
    PublicUrl: string
    /// The origins of the proxies ([Proxies]) as browsers reach them: a sign-in
    /// begun through one returns there (matched by the Host of the request).
    ProxyUrls: string list
}

/// Proxies players connect through when they cannot reach this server directly
/// (docs/DeploymentRu.md, «Прокси»). Their forwarded client addresses count on
/// the authentication host; a game connection from one takes the address the
/// player signed in from; their own addresses are never range-banned.
type ProxySettings = {
    /// Addresses or CIDR ranges, like range bans: "203.0.113.7", "2001:db8::/48".
    Trusted: string list
}

/// Who may register is not a setting: the panel and the console change the
/// registration mode at run time (RegistrationMode, stored in the database).
type AuthenticationSettings = {
    Steam: SteamSettings
    Listener: HttpListenerSettings
    Service: AccountServiceOptions
}

/// The web panel host. By default it listens on loopback only; see docs/AdminPanelRu.md.
type AdminSettings = {
    Enabled: bool
    MaxConnections: int
    Listener: HttpListenerSettings
    Service: AdminServiceOptions
}

/// Word-list filtering of names and chat text. Anti-spam limits are in Runtime.Chat.
type ModerationSettings = {
    Enabled: bool
    /// Separate TOML with words/substrings/exceptions, relative to the working directory.
    RulesPath: string
}

/// Restarting the game part (ENet transport, runtime, mark writer) after it fails;
/// HTTP, accounts and the admin panel keep running meanwhile.
type RecoverySettings = {
    /// Delay before the restart after the first failure in the window; doubles per further failure.
    InitialDelayMs: int
    MaxDelayMs: int
    /// Failures allowed within WindowSeconds; one more stops the server. 0 never restarts.
    MaxRestarts: int
    WindowSeconds: int
}

type ApplicationConfig = {
    Server: ServerConfig
    Runtime: ServerRuntimeOptions
    Recovery: RecoverySettings
    Database: SqliteAccountStoreConfig
    Authentication: AuthenticationSettings
    Admin: AdminSettings
    Logging: LoggingSettings
    Moderation: ModerationSettings
    Identity: IdentityOptions
    Announcements: AnnouncementOptions
    GroundMarks: GroundMarkOptions
    Guilds: GuildOptions
    Proxies: ProxySettings
    Phantoms: PhantomOptions
}

[<RequireQualifiedAccess>]
type LaunchCommand =
    /// The file as read, and its game part as the runtime receives it.
    | Run of ApplicationConfig * GameSettings
    | WriteConfig of string
    | Help

[<RequireQualifiedAccess>]
type internal FileReadError =
    | Missing
    | TooLarge
    | Failed of exn

[<RequireQualifiedAccess>]
module Configuration =
    let defaults = {
        Server = ServerConfig.defaults
        Runtime = ServerRuntimeOptions.defaults
        Recovery = {
            InitialDelayMs = 1000
            MaxDelayMs = 30000
            MaxRestarts = 5
            WindowSeconds = 600
        }
        Database = SqliteAccountStoreConfig.defaults
        Authentication = {
            Steam = {
                Enabled = false
                PublicUrl = ""
                ProxyUrls = []
            }
            Listener = {
                ListenUrl = "http://127.0.0.1:8779"
                AllowInsecureLoopback = true
                AllowInsecureRemote = false
                CertificatePath = ""
                TrustForwardedHeaders = false

                RequestsPerMinute = 120
                RequestTimeoutSeconds = 15
            }
            Service = AuthService.defaults
        }
        Admin = {
            Enabled = true
            MaxConnections = 64
            Listener = {
                ListenUrl = "http://127.0.0.1:8780"
                AllowInsecureLoopback = true
                AllowInsecureRemote = false
                CertificatePath = ""
                TrustForwardedHeaders = false

                RequestsPerMinute = 600
                RequestTimeoutSeconds = 15
            }
            Service = AdminService.defaults
        }
        Logging = ServerLogging.defaults
        Moderation = { Enabled = true; RulesPath = "moderation.toml" }
        Identity = IdentityOptions.defaults
        Announcements = AnnouncementOptions.defaults
        GroundMarks = GroundMarkOptions.defaults
        Guilds = GuildOptions.defaults
        Proxies = { Trusted = [] }
        Phantoms = PhantomOptions.defaults
    }

    // Each [[table array]] entry starts from these defaults, like a section does.
    let private listItemDefaults =
        dict [
            typeof<ScheduledAnnouncement>, box {
                Text = ""
                Kind = "Announcement"
                DelaySeconds = 0
                IntervalSeconds = 0
            }
        ]

    let private isList (target: Type) =
        target.IsGenericType && target.GetGenericTypeDefinition() = typedefof<list<_>>

    let private makeList (listType: Type) (items: obj list) =
        let cases = FSharpType.GetUnionCases listType
        let empty = cases |> Array.find (fun case -> case.Name = "Empty")
        let cons = cases |> Array.find (fun case -> case.Name = "Cons")
        List.foldBack (fun item tail -> FSharpValue.MakeUnion(cons, [| item; tail |])) items (FSharpValue.MakeUnion(empty, [||]))

    let private overlayScalar (current: obj) (input: obj) (target: Type) invalid =
        if current :? IPAddress then
            match input with
            | :? string as text ->
                match IPAddress.TryParse text with
                | true, address -> Ok (box address)
                | false, _ -> invalid ()
            | _ -> invalid ()
        elif target = typeof<string> || target = typeof<bool> then
            if input.GetType() = target then Ok input else invalid ()
        else
            let floating = target = typeof<single> || target = typeof<double>
            let number = input :? int64 || (floating && input :? double)
            if not number then invalid ()
            else
                try
                    let value = Convert.ChangeType(input, target, CultureInfo.InvariantCulture)
                    if floating && not (Double.IsFinite(Convert.ToDouble(value, CultureInfo.InvariantCulture))) then invalid ()
                    else Ok value
                with
                | :? OverflowException | :? InvalidCastException -> invalid ()

    // Records remain immutable domain settings. TOML overrides only supplied fields.
    let rec private overlay path (current: obj) (input: obj) : Result<obj, string> =
        let target = current.GetType()
        let invalid () = Error $"Invalid TOML value or type: {path}"
        if isList target then
            let element = target.GetGenericArguments()[0]
            match input, listItemDefaults.TryGetValue element with
            // A list of strings is an inline array of strings.
            | (:? TomlArray as values), _ when element = typeof<string> ->
                if values |> Seq.forall (fun value -> value :? string) then Ok (makeList target (List.ofSeq values))
                else invalid ()
            | (:? TomlTableArray as tables), (true, template) ->
                let items =
                    tables
                    |> Seq.mapi (fun index table -> overlay $"{path.TrimEnd('.')}[{index}]." template table)
                    |> Seq.toList

                match items |> List.tryPick (function Error error -> Some error | Ok _ -> None) with
                | Some error -> Error error
                | None -> Ok (makeList target (items |> List.choose (function Ok value -> Some value | Error _ -> None)))
            // An exported empty list is written as an inline empty array.
            | (:? TomlArray as values), (true, _) when values.Count = 0 -> Ok (makeList target [])
            | _ -> invalid ()
        elif FSharpType.IsRecord target then
            match input with
            | :? TomlTable as table ->
                let fields = FSharpType.GetRecordFields target
                match table.Keys |> Seq.tryFind (fun name -> fields |> Array.forall (fun field -> field.Name <> name)) with
                | Some name -> Error $"Unknown setting: {path}{name}"
                | None ->
                    let values =
                        fields
                        |> Array.map (fun field ->
                            let value = field.GetValue current
                            match table.TryGetValue field.Name with
                            | true, replacement -> overlay (path + field.Name + ".") value replacement
                            | false, _ -> Ok value)

                    match values |> Array.tryPick (function Error error -> Some error | Ok _ -> None) with
                    | Some error -> Error error
                    | None -> Ok (FSharpValue.MakeRecord(target, values |> Array.choose (function Ok value -> Some value | Error _ -> None)))
            | _ -> invalid ()
        else overlayScalar current input target invalid

    let rec private toTableValue (value: obj) : obj =
        let valueType = value.GetType()
        if isList valueType && valueType.GetGenericArguments()[0] = typeof<string> then
            let values = TomlArray()
            for item in value :?> System.Collections.IEnumerable do
                values.Add item
            box values
        elif isList valueType then
            let tables = TomlTableArray()
            for item in value :?> System.Collections.IEnumerable do
                tables.Add(toTableValue item :?> TomlTable)
            box tables
        elif FSharpType.IsRecord valueType then
            let table = TomlTable()
            for field in FSharpType.GetRecordFields valueType do
                table.Add(field.Name, toTableValue (field.GetValue value))
            box table
        elif value :? IPAddress then box (string value)
        else value

    [<Literal>]
    let private MaxConfigurationBytes = 65536

    // The caller owns the stream. Reading cap+1 bytes detects growth without
    // trusting an earlier pathname or stream length observation.
    let internal readBounded (stream: Stream) maxBytes =
        let bytes = Array.zeroCreate<byte> (maxBytes + 1)
        let mutable length = 0
        let mutable ended = false
        while not ended && length < bytes.Length do
            let read = stream.Read(bytes, length, bytes.Length - length)
            if read = 0 then ended <- true
            else length <- length + read
        if length > maxBytes then Error FileReadError.TooLarge
        else
            use memory = new MemoryStream(bytes, 0, length, false)
            use reader = new StreamReader(memory, System.Text.Encoding.UTF8, true)
            Ok (reader.ReadToEnd())

    let private readText path maxBytes =
        try
            use stream = File.OpenRead path
            readBounded stream maxBytes
        with
        | :? FileNotFoundException | :? DirectoryNotFoundException -> Error FileReadError.Missing
        | :? IOException as error -> Error(FileReadError.Failed error)
        | :? UnauthorizedAccessException as error -> Error(FileReadError.Failed error)
        | :? ArgumentException as error -> Error(FileReadError.Failed error)
        | :? SecurityException as error -> Error(FileReadError.Failed error)

    // DOM deserialization alone does not reject duplicate TOML keys. Only the
    // dependency parser/deserializer is inside this malformed-input adapter.
    let private parseTable description (sourceName: string) (source: string) =
        try
            let document = Tomlyn.Parsing.SyntaxParser.Parse(source, sourceName, true)
            if document.HasErrors then Error $"Invalid {description}: {document.Diagnostics}"
            else Ok (TomlSerializer.Deserialize<TomlTable>(source))
        with :? TomlException as error -> Error $"Invalid {description}: {error.Message}"

    let private load path =
        match readText path MaxConfigurationBytes with
        | Error FileReadError.Missing -> Error $"Cannot read configuration: File not found: {path}"
        | Error FileReadError.TooLarge -> Error "Server configuration must not exceed 65536 bytes."
        | Error(FileReadError.Failed error) -> Error $"Cannot read configuration: {error.Message}"
        | Ok source ->
            parseTable "TOML configuration" path source
            |> Result.bind (fun table -> overlay "" (box defaults) (box table) |> Result.map unbox<ApplicationConfig>)

    let writeDefaults path =
        let source = TomlSerializer.Serialize(toTableValue (box defaults))
        try
            File.WriteAllText(path, source)
            Ok ()
        with
        | :? ArgumentException as error -> Error error.Message
        | :? IOException as error -> Error error.Message
        | :? UnauthorizedAccessException as error -> Error error.Message
        | :? SecurityException as error -> Error error.Message

    let private isLoopback (uri: Uri) = uri.Host = "127.0.0.1" || uri.Host = "[::1]" || uri.Host = "::1"

    /// One rule for both hosts: an absolute URL of scheme, host and port only;
    /// plain HTTP only on an explicitly enabled literal loopback or by explicit choice.
    let private listenUrl (section: string) (url: string) allowLoopback allowRemote =
        match Uri.TryCreate(url, UriKind.Absolute) with
        | false, _ -> Error $"{section}.ListenUrl must be an absolute HTTP(S) URL."
        | true, uri when not (String.IsNullOrEmpty uri.UserInfo) || uri.AbsolutePath <> "/"
                         || not (String.IsNullOrEmpty uri.Query) || not (String.IsNullOrEmpty uri.Fragment) ->
            Error $"{section} URL must contain only scheme, host and port."
        | true, uri when uri.Scheme <> "http" && uri.Scheme <> "https" -> Error $"{section} requires HTTP(S)."
        | true, uri when uri.Scheme = "http" && not (allowRemote || (allowLoopback && isLoopback uri)) ->
            Error $"Remote HTTP {section} requires {section}.AllowInsecureRemote; otherwise use HTTPS or explicitly enabled literal loopback."
        | true, uri -> Ok uri

    [<Literal>]
    let MaxRequestsPerMinute = 100000

    [<Literal>]
    let MaxRequestTimeoutSeconds = 120

    [<Literal>]
    let MaxAdminConnections = 10000

    /// An hour: a longer restart delay only hides the failure.
    [<Literal>]
    let MaxRestartDelayMs = 3600000

    [<Literal>]
    let MaxRestarts = 1000

    /// A day.
    [<Literal>]
    let MaxRestartWindowSeconds = 86400

    let private recovery (settings: RecoverySettings) = [
        if settings.InitialDelayMs < 0 || settings.InitialDelayMs > MaxRestartDelayMs then
            $"Recovery.InitialDelayMs must be 0..{MaxRestartDelayMs}."
        if settings.MaxDelayMs < settings.InitialDelayMs || settings.MaxDelayMs > MaxRestartDelayMs then
            $"Recovery.MaxDelayMs must be InitialDelayMs..{MaxRestartDelayMs}."
        if settings.MaxRestarts < 0 || settings.MaxRestarts > MaxRestarts then $"Recovery.MaxRestarts must be 0..{MaxRestarts}."
        if settings.WindowSeconds < 1 || settings.WindowSeconds > MaxRestartWindowSeconds then
            $"Recovery.WindowSeconds must be 1..{MaxRestartWindowSeconds}."
    ]

    /// The supervisor's policy; the settings come checked.
    let restartPolicy (settings: RecoverySettings) : RestartPolicy = {
        InitialDelay = TimeSpan.FromMilliseconds(float settings.InitialDelayMs)
        MaxDelay = TimeSpan.FromMilliseconds(float settings.MaxDelayMs)
        MaxRestarts = settings.MaxRestarts
        Window = TimeSpan.FromSeconds(float settings.WindowSeconds)
    }

    let private listener section (settings: HttpListenerSettings) = [
        match listenUrl section settings.ListenUrl settings.AllowInsecureLoopback settings.AllowInsecureRemote with
        | Error error -> error
        | Ok _ -> ()
        if settings.RequestsPerMinute < 1 || settings.RequestsPerMinute > MaxRequestsPerMinute then
            $"{section}.RequestsPerMinute must be 1..{MaxRequestsPerMinute}."
        if settings.RequestTimeoutSeconds < 1 || settings.RequestTimeoutSeconds > MaxRequestTimeoutSeconds then
            $"{section}.RequestTimeoutSeconds must be 1..{MaxRequestTimeoutSeconds}."
    ]

    let private port (url: string) =
        match Uri.TryCreate(url, UriKind.Absolute) with
        | true, uri -> Some uri.Port
        | false, _ -> None

    /// The [Proxies] the file trusts; validate has refused any that does not parse.
    let trustedProxies (config: ApplicationConfig) =
        config.Proxies.Trusted |> List.choose (AddressRange.parse >> Result.toOption)

    /// The one check of the whole file: every section's own rules and the rules
    /// between sections. The owners that receive the settings trust them.
    let private validate (config: ApplicationConfig) =
        let authentication = config.Authentication
        let admin = config.Admin
        let errors = [
            yield! SqliteAccountStoreConfig.validate config.Database
            yield! recovery config.Recovery
            yield! listener "Authentication.Listener" authentication.Listener
            yield! AuthService.validate authentication.Service
            if authentication.Steam.Enabled then
                match listenUrl "Authentication.Steam.PublicUrl" authentication.Steam.PublicUrl true authentication.Listener.AllowInsecureRemote with
                | Error _ ->
                    "Authentication.Steam.PublicUrl must be an origin (scheme, host and port) that players' browsers reach; "
                    + "plain HTTP only on literal loopback or with Authentication.Listener.AllowInsecureRemote."
                | Ok _ -> ()
                for url in authentication.Steam.ProxyUrls do
                    match listenUrl "Authentication.Steam.ProxyUrls" url true authentication.Listener.AllowInsecureRemote with
                    | Error _ -> $"Authentication.Steam.ProxyUrls: \"{url}\" is not an origin (scheme, host and port) that browsers reach."
                    | Ok _ -> ()
            for proxy in config.Proxies.Trusted do
                match AddressRange.parse proxy with
                | Error _ -> $"Proxies.Trusted: \"{proxy}\" is not an address or a CIDR range."
                | Ok _ -> ()
            if admin.Enabled then
                yield! listener "Admin.Listener" admin.Listener
                yield! AdminService.validate admin.Service
                if admin.MaxConnections < 1 || admin.MaxConnections > MaxAdminConnections then
                    $"Admin.MaxConnections must be 1..{MaxAdminConnections}."
                match port admin.Listener.ListenUrl, port authentication.Listener.ListenUrl with
                | Some panel, Some accounts when panel = accounts ->
                    "Admin.Listener.ListenUrl and Authentication.Listener.ListenUrl must use different ports."
                | _ -> ()
            match ServerLogging.validate config.Logging with Ok () -> () | Error error -> error
            if config.Moderation.Enabled && String.IsNullOrWhiteSpace config.Moderation.RulesPath then
                "Moderation.RulesPath must be set when moderation is enabled."
        ]
        match errors, GameSettings.create config.Server config.Runtime config.Identity config.Announcements config.GroundMarks config.Guilds with
        | [], Ok game ->
            GameSettings.withPhantoms config.Phantoms game
            |> Result.map (fun game -> config, GameSettings.withTrustedProxies (trustedProxies config) game)
            |> Result.mapError (String.concat "\n")
        | errors, Ok _ -> Error (String.concat " " errors)
        | errors, Error game -> Error (String.concat " " (errors @ game))

    /// The effective settings as TOML, for the read-only configuration page.
    let render (config: ApplicationConfig) = TomlSerializer.Serialize(toTableValue (box config))

    [<Literal>]
    let private MaxRulesBytes = 1048576

    let private stringList (table: TomlTable) name =
        match table.TryGetValue name with
        | false, _ -> Ok []
        | true, (:? TomlArray as values) ->
            let items = values |> Seq.toList
            if items |> List.forall (fun item -> item :? string) then Ok (items |> List.map unbox<string>)
            else Error $"Moderation rules: {name} must contain only strings."
        | true, _ -> Error $"Moderation rules: {name} must be an array of strings."

    let private tierSource (table: TomlTable) scope =
        let known = set ["words"; "substrings"; "exceptions"]
        match table.Keys |> Seq.tryFind (fun key -> not (known.Contains key)) with
        | Some key -> Error $"Unknown moderation setting: {scope}{key}"
        | None ->
            match stringList table "words", stringList table "substrings", stringList table "exceptions" with
            | Ok words, Ok substrings, Ok exceptions ->
                Ok {
                    Words = words
                    Substrings = substrings
                    Exceptions = exceptions
                }
            | Error error, _, _ | _, Error error, _ | _, _, Error error -> Error error

    let private section (table: TomlTable) name =
        match table.TryGetValue name with
        | false, _ -> Ok (TomlTable())
        | true, (:? TomlTable as value) -> Ok value
        | true, _ -> Error $"Moderation rules: [{name}] must be a table."

    /// Parses the separate word-list file: [block] refuses, [flag] marks.
    /// Top-level words/substrings/exceptions are the block tier of older files.
    let parseModeration (source: string) =
        match parseTable "moderation TOML" "moderation" source with
        | Error error -> Error error
        | Ok table ->
            let legacy = TomlTable()
            let mutable unknown = None
            for key in table.Keys do
                match key with
                | "block" | "flag" -> ()
                | "words" | "substrings" | "exceptions" -> legacy.Add(key, table[key])
                | other -> unknown <- Some other
            match unknown with
            | Some key -> Error $"Unknown moderation setting: {key}"
            | None ->
                let merge (first: ModerationSource) (second: ModerationSource) =
                    {
                        Words = first.Words @ second.Words
                        Substrings = first.Substrings @ second.Substrings
                        Exceptions = first.Exceptions @ second.Exceptions
                    }
                let tiers =
                    section table "block"
                    |> Result.bind (fun block ->
                        section table "flag"
                        |> Result.bind (fun flag ->
                            tierSource legacy ""
                            |> Result.bind (fun top ->
                                tierSource block "block."
                                |> Result.bind (fun blocked ->
                                    tierSource flag "flag."
                                    |> Result.map (fun flagged -> merge top blocked, flagged)))))
                tiers |> Result.map (fun (block, flag) -> Moderation.create block |> Moderation.withFlags flag)

    let internal readModerationSource path = readText path MaxRulesBytes

    /// Disabled moderation uses empty rules. A missing file is a warning: the
    /// server runs with an empty list rather than refusing to start.
    let loadModeration (settings: ModerationSettings) : Result<ModerationRules * string option, string> =
        if not settings.Enabled then Ok (Moderation.empty, None)
        else
            match readModerationSource settings.RulesPath with
            | Error FileReadError.Missing ->
                Ok (Moderation.empty, Some $"Moderation rules file not found: {settings.RulesPath}; the word list is empty.")
            | Error FileReadError.TooLarge -> Error "Moderation rules must not exceed 1 MiB."
            | Error(FileReadError.Failed error) -> Error $"Cannot read moderation rules: {error.Message}"
            | Ok source ->
                parseModeration source
                |> Result.map (fun rules ->
                    rules, (if rules.IsEmpty && not rules.HasFlags then Some "Moderation rules contain no words or substrings." else None))

    [<Literal>]
    let private MaxPseudonymsBytes = 65536

    /// The pseudonym file: version = 1 and names = [...]. Entries that break the
    /// rules of Pseudonym.create or repeat are skipped and counted.
    let parsePseudonyms (source: string) =
        match parseTable "pseudonym TOML" "pseudonyms" source with
        | Error error -> Error error
        | Ok table ->
            match table.Keys |> Seq.tryFind (fun key -> key <> "version" && key <> "names") with
            | Some key -> Error $"Unknown pseudonym setting: {key}"
            | None ->
                match table.TryGetValue "version", table.TryGetValue "names" with
                | (true, (:? int64 as version)), _ when version <> 1L -> Error "Unsupported pseudonym file version."
                | (true, value), _ when not (value :? int64) -> Error "Pseudonym file version must be a number."
                | _, (true, (:? TomlArray as values)) ->
                    let names =
                        values
                        |> Seq.choose (function
                            | :? string as text -> Pseudonym.create text |> Result.toOption
                            | _ -> None)
                        |> List.ofSeq

                    match PseudonymDictionary.create names with
                    | ValueNone -> Error "Pseudonym file has no valid names."
                    | ValueSome dictionary -> Ok (dictionary, values.Count - dictionary.Count)
                | _, (true, _) -> Error "Pseudonym names must be an array of strings."
                | _, (false, _) -> Error "Pseudonym file has no names."

    /// Never stops the server: a missing, oversized, broken or empty file
    /// falls back to the built-in list with a warning, like the client does.
    let loadPseudonyms (options: IdentityOptions) : PseudonymDictionary * string option =
        let fallback reason = PseudonymDictionary.builtIn, Some $"{reason}; using the {PseudonymDictionary.builtIn.Count} built-in pseudonyms."
        if String.IsNullOrWhiteSpace options.PseudonymsPath then fallback $"Pseudonym file not found: {options.PseudonymsPath}"
        else
            match readText options.PseudonymsPath MaxPseudonymsBytes with
            | Error FileReadError.Missing -> fallback $"Pseudonym file not found: {options.PseudonymsPath}"
            | Error FileReadError.TooLarge -> fallback "Pseudonym file exceeds 64 KiB"
            | Error(FileReadError.Failed error) -> fallback $"Cannot read pseudonyms: {error.Message}"
            | Ok source ->
                match parsePseudonyms source with
                | Error error -> fallback error
                | Ok (dictionary, 0) -> dictionary, None
                | Ok (dictionary, skipped) -> dictionary, Some $"Pseudonym file: {skipped} invalid or repeated entries skipped."

    let rec private arguments configFile port (remainingArgs: string list) =
        match remainingArgs with
        | [] ->
            // --port replaces the file's value before the one check.
            match configFile with
            | None -> Ok defaults
            | Some path -> load path
            |> Result.map (fun config ->
                match port with
                | None -> config
                | Some value -> { config with Server = { config.Server with Port = value } })
            |> Result.bind validate
            |> Result.map LaunchCommand.Run
        | "--help" :: _ | "-h" :: _ -> Ok LaunchCommand.Help
        | "--config" :: path :: remaining -> arguments (Some path) port remaining
        | "--port" :: value :: remaining ->
            match UInt16.TryParse value with
            | true, number when number > 0us -> arguments configFile (Some number) remaining
            | true, _ | false, _ -> Error "--port must be between 1 and 65535."
        | ["--write-config"; path] when configFile.IsNone && port.IsNone -> Ok (LaunchCommand.WriteConfig path)
        | unknown :: _ -> Error (sprintf "Unknown or incomplete argument: %s" unknown)

    let parse args = arguments None None (Array.toList args)
