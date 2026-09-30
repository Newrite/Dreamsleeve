namespace Dreamsleeve.Server

open System
open System.IO
open System.Net
open System.Globalization
open Microsoft.FSharp.Reflection
open Tomlyn
open Tomlyn.Model
open Dreamsleeve.Server.Domain
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Infrastructure

type AuthenticationSettings = {
    ListenUrl: string
    AllowInsecureRemote: bool
    AllowInsecureLoopback: bool
    AllowRegistration: bool
    CertificatePath: string
    /// X-Forwarded-For/Proto from a proxy on this machine only.
    TrustForwardedHeaders: bool
    RequestsPerMinute: int
    RequestTimeoutSeconds: int
    Service: AccountServiceOptions
}

/// The web panel host. By default it listens on loopback only; see docs/AdminPanelRu.md.
type AdminSettings = {
    Enabled: bool
    ListenUrl: string
    AllowInsecureLoopback: bool
    /// Passwords without TLS outside loopback only by explicit choice, like authentication.
    AllowInsecureRemote: bool
    /// Direct HTTPS without a reverse proxy.
    CertificatePath: string
    /// true only behind a proxy on loopback.
    TrustForwardedHeaders: bool
    SessionHours: int
    CodeLifetimeMinutes: int
    /// Sign-in, setup and reset attempts per client address and per name, each minute.
    LoginAttemptsPerMinute: int
    /// All other requests per client address and minute.
    RequestsPerMinute: int
    MaxConnections: int
    RequestTimeoutSeconds: int
}

/// Word-list filtering of names and chat text. Anti-spam limits are in Runtime.Chat.
type ModerationSettings = {
    Enabled: bool
    /// Separate TOML with words/substrings/exceptions, relative to the working directory.
    RulesPath: string
}

type ApplicationConfig = {
    Server: ServerConfig
    Runtime: ServerRuntimeOptions
    Database: SqliteAccountStoreConfig
    Authentication: AuthenticationSettings
    Admin: AdminSettings
    Logging: LoggingSettings
    Moderation: ModerationSettings
    Identity: IdentityOptions
    Announcements: AnnouncementOptions
    GroundMarks: GroundMarkOptions
}

[<RequireQualifiedAccess>]
type LaunchCommand =
    | Run of ApplicationConfig
    | WriteConfig of string
    | Help

[<RequireQualifiedAccess>]
module Configuration =
    let defaults = {
        Server = ServerConfig.defaults
        Runtime = ServerRuntimeOptions.defaults
        Database = { DatabasePath = "data/dreamsleeve.db"; BusyTimeoutSeconds = 5 }
        Authentication = {
            ListenUrl = "http://127.0.0.1:8779"; AllowInsecureLoopback = true; AllowInsecureRemote = false; AllowRegistration = true
            CertificatePath = ""; TrustForwardedHeaders = false; RequestsPerMinute = 120; RequestTimeoutSeconds = 15
            Service = AuthService.defaults
        }
        Admin = {
            Enabled = true; ListenUrl = "http://127.0.0.1:8780"; AllowInsecureLoopback = true; AllowInsecureRemote = false
            CertificatePath = ""; TrustForwardedHeaders = false; SessionHours = AdminService.defaults.SessionHours
            CodeLifetimeMinutes = AdminService.defaults.CodeLifetimeMinutes; LoginAttemptsPerMinute = AdminService.defaults.LoginAttemptsPerMinute
            RequestsPerMinute = 600; MaxConnections = 64; RequestTimeoutSeconds = 15
        }
        Logging = ServerLogging.defaults
        Moderation = { Enabled = true; RulesPath = "moderation.toml" }
        Identity = IdentityOptions.defaults
        Announcements = AnnouncementOptions.defaults
        GroundMarks = GroundMarkOptions.defaults
    }

    // Each [[table array]] entry starts from these defaults, like a section does.
    let private listItemDefaults =
        dict [ typeof<ScheduledAnnouncement>, box { Text = ""; Kind = "Announcement"; DelaySeconds = 0; IntervalSeconds = 0 } ]

    let private isList (target: Type) =
        target.IsGenericType && target.GetGenericTypeDefinition() = typedefof<list<_>>

    let private makeList (listType: Type) (items: obj list) =
        let cases = FSharpType.GetUnionCases listType
        let empty = cases |> Array.find (fun case -> case.Name = "Empty")
        let cons = cases |> Array.find (fun case -> case.Name = "Cons")
        List.foldBack (fun item tail -> FSharpValue.MakeUnion(cons, [| item; tail |])) items (FSharpValue.MakeUnion(empty, [||]))

    // Records remain immutable domain settings. TOML overrides only supplied fields.
    let rec private overlay path (current: obj) (input: obj) : Result<obj, string> =
        let target = current.GetType()
        let invalid () = Error $"Invalid TOML value or type: {path}"
        if isList target then
            let element = target.GetGenericArguments()[0]
            match input, listItemDefaults.TryGetValue element with
            | (:? TomlTableArray as tables), (true, template) ->
                let items = tables |> Seq.mapi (fun index table -> overlay $"{path.TrimEnd('.')}[{index}]." template table) |> Seq.toList
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
                    let values = fields |> Array.map (fun field ->
                        let value = field.GetValue current
                        match table.TryGetValue field.Name with
                        | true, replacement -> overlay (path + field.Name + ".") value replacement
                        | false, _ -> Ok value)
                    match values |> Array.tryPick (function Error error -> Some error | Ok _ -> None) with
                    | Some error -> Error error
                    | None -> Ok (FSharpValue.MakeRecord(target, values |> Array.choose (function Ok value -> Some value | Error _ -> None)))
            | _ -> invalid ()
        elif current :? IPAddress then
            match input with
            | :? string as text ->
                match IPAddress.TryParse text with true, address -> Ok (box address) | false, _ -> invalid ()
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

    let rec private toTableValue (value: obj) : obj =
        let valueType = value.GetType()
        if isList valueType then
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

    let private load path =
        try
            if FileInfo(path).Length > 65536L then Error "Server configuration must not exceed 65536 bytes."
            else
                let source = File.ReadAllText path
                // DOM deserialization alone does not reject duplicate TOML keys.
                let document = Tomlyn.Parsing.SyntaxParser.Parse(source, path, true)
                if document.HasErrors then Error $"Invalid TOML configuration: {document.Diagnostics}"
                else
                    let table = TomlSerializer.Deserialize<TomlTable>(source)
                    overlay "" (box defaults) (box table) |> Result.map unbox<ApplicationConfig>
        with
        | :? TomlException as error -> Error $"Invalid TOML configuration: {error.Message}"
        | :? ArgumentException as error -> Error $"Cannot read configuration: {error.Message}"
        | :? IOException as error -> Error $"Cannot read configuration: {error.Message}"
        | :? UnauthorizedAccessException as error -> Error $"Cannot read configuration: {error.Message}"

    let writeDefaults path =
        try
            File.WriteAllText(path, TomlSerializer.Serialize(toTableValue (box defaults)))
            Ok ()
        with
        | :? ArgumentException as error -> Error error.Message
        | :? IOException as error -> Error error.Message
        | :? UnauthorizedAccessException as error -> Error error.Message

    /// The admin service takes the password cost of player accounts.
    let adminService (config: ApplicationConfig) : AdminServiceOptions =
        { AdminService.defaults with
            PasswordIterations = config.Authentication.Service.PasswordIterations
            SessionHours = config.Admin.SessionHours
            CodeLifetimeMinutes = config.Admin.CodeLifetimeMinutes
            LoginAttemptsPerMinute = config.Admin.LoginAttemptsPerMinute }

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

    let private validateAdmin (config: ApplicationConfig) =
        let admin = config.Admin
        if not admin.Enabled then Ok ()
        elif admin.RequestsPerMinute < 1 || admin.RequestsPerMinute > 100000 || admin.MaxConnections < 1 || admin.MaxConnections > 10000
             || admin.RequestTimeoutSeconds < 1 || admin.RequestTimeoutSeconds > 120 || isNull admin.CertificatePath then
            Error "Invalid admin request limits or certificate path."
        elif not (AdminService.validate (adminService config)).IsEmpty then
            Error (String.concat " " (AdminService.validate (adminService config)))
        else
            match listenUrl "Admin" admin.ListenUrl admin.AllowInsecureLoopback admin.AllowInsecureRemote,
                  Uri.TryCreate(config.Authentication.ListenUrl, UriKind.Absolute) with
            | Error error, _ -> Error error
            | Ok panel, (true, authentication) when panel.Port <> 0 && panel.Port = authentication.Port ->
                Error "Admin.ListenUrl and Authentication.ListenUrl must use different ports."
            | Ok _, _ -> Ok ()

    let private validate (config: ApplicationConfig) =
        if isNull (box config.Server) || isNull (box config.Runtime) || isNull (box config.Database) || isNull (box config.Authentication) || isNull (box config.Logging)
           || isNull (box config.Admin) || isNull config.Admin.ListenUrl
           || isNull (box config.Server.ChatInput) || isNull (box config.Server.PlayerInput) || isNull (box config.Runtime.Player)
           || isNull (box config.Runtime.Chat) || isNull (box config.Runtime.Presence)
           || isNull (box config.Authentication.Service) || isNull (box config.Moderation) || isNull (box config.Announcements)
           || isNull (box config.GroundMarks) || isNull (box config.Identity) then
            Error "Configuration sections cannot be null."
        elif not (GroundMarkOptions.validate config.GroundMarks).IsEmpty then
            Error (String.concat " " (GroundMarkOptions.validate config.GroundMarks))
        elif not (IdentityOptions.validate config.Identity).IsEmpty then
            Error (String.concat " " (IdentityOptions.validate config.Identity))
        elif not (Single.IsFinite config.Runtime.Presence.VisibilityDistance) || config.Runtime.Presence.VisibilityDistance < 0.0f then
            Error "Presence.VisibilityDistance must be finite and non-negative."
        elif config.Runtime.MaxSessions > config.Server.PeerLimit then
            Error "Runtime.MaxSessions cannot exceed Server.PeerLimit."
        elif config.Runtime.MaxSessions > config.Server.MaxInitialPlayers then
            Error "Server.MaxInitialPlayers must include every admitted session."
        elif config.Runtime.Chat.HistoryCapacity > config.Server.MaxRecentMessages then
            Error "Server.MaxRecentMessages must include the retained chat history."
        elif String.IsNullOrWhiteSpace config.Database.DatabasePath
             || config.Database.BusyTimeoutSeconds < 1 || config.Database.BusyTimeoutSeconds > 30 then
            Error "Database path must be nonempty and busy timeout 1..30 seconds."
        elif config.Authentication.RequestsPerMinute < 1 || config.Authentication.RequestsPerMinute > 100000
             || config.Authentication.RequestTimeoutSeconds < 1 || config.Authentication.RequestTimeoutSeconds > 120
             || isNull config.Authentication.CertificatePath then
            Error "Invalid authentication request limits or certificate path."
        elif isNull config.Moderation.RulesPath || (config.Moderation.Enabled && String.IsNullOrWhiteSpace config.Moderation.RulesPath) then
            Error "Moderation.RulesPath must be set when moderation is enabled."
        elif not (AuthService.validate config.Authentication.Service).IsEmpty then
            Error (String.concat " " (AuthService.validate config.Authentication.Service))
        else
            listenUrl "Authentication" config.Authentication.ListenUrl config.Authentication.AllowInsecureLoopback config.Authentication.AllowInsecureRemote
            |> Result.bind (fun _ -> validateAdmin config)
            |> Result.bind (fun () -> ServerLogging.validate config.Logging)
            |> Result.bind (fun () -> ServerConfig.validate config.Server |> Result.mapError (String.concat " "))
            |> Result.map (fun _ -> config)

    /// The effective settings as TOML, for the read-only configuration page.
    let render (config: ApplicationConfig) = TomlSerializer.Serialize(toTableValue (box config))

    [<Literal>]
    let private MaxRulesBytes = 1048576L

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
            | Ok words, Ok substrings, Ok exceptions -> Ok { Words = words; Substrings = substrings; Exceptions = exceptions }
            | Error error, _, _ | _, Error error, _ | _, _, Error error -> Error error

    let private section (table: TomlTable) name =
        match table.TryGetValue name with
        | false, _ -> Ok (TomlTable())
        | true, (:? TomlTable as value) -> Ok value
        | true, _ -> Error $"Moderation rules: [{name}] must be a table."

    /// Parses the separate word-list file: [block] refuses, [flag] marks.
    /// Top-level words/substrings/exceptions are the block tier of older files.
    let parseModeration (source: string) =
        let document = Tomlyn.Parsing.SyntaxParser.Parse(source, "moderation", true)
        if document.HasErrors then Error $"Invalid moderation TOML: {document.Diagnostics}"
        else
            let table = TomlSerializer.Deserialize<TomlTable>(source)
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
                    { Words = first.Words @ second.Words; Substrings = first.Substrings @ second.Substrings
                      Exceptions = first.Exceptions @ second.Exceptions }
                let tiers =
                    section table "block" |> Result.bind (fun block ->
                    section table "flag" |> Result.bind (fun flag ->
                    tierSource legacy "" |> Result.bind (fun top ->
                    tierSource block "block." |> Result.bind (fun blocked ->
                    tierSource flag "flag." |> Result.map (fun flagged -> merge top blocked, flagged)))))
                tiers |> Result.map (fun (block, flag) -> Moderation.create block |> Moderation.withFlags flag)

    /// Disabled moderation uses empty rules. A missing file is a warning: the
    /// server runs with an empty list rather than refusing to start.
    let loadModeration (settings: ModerationSettings) : Result<ModerationRules * string option, string> =
        if not settings.Enabled then Ok (Moderation.empty, None)
        else
            try
                let file = FileInfo settings.RulesPath
                if not file.Exists then
                    Ok (Moderation.empty, Some $"Moderation rules file not found: {file.FullName}; the word list is empty.")
                elif file.Length > MaxRulesBytes then Error "Moderation rules must not exceed 1 MiB."
                else
                    parseModeration (File.ReadAllText file.FullName)
                    |> Result.map (fun rules ->
                        rules, (if rules.IsEmpty && not rules.HasFlags then Some "Moderation rules contain no words or substrings." else None))
            with
            | :? TomlException as error -> Error $"Invalid moderation TOML: {error.Message}"
            | :? IOException as error -> Error $"Cannot read moderation rules: {error.Message}"
            | :? UnauthorizedAccessException as error -> Error $"Cannot read moderation rules: {error.Message}"
            | :? ArgumentException as error -> Error $"Cannot read moderation rules: {error.Message}"

    [<Literal>]
    let private MaxPseudonymsBytes = 65536L

    /// The pseudonym file: version = 1 and names = [...]. Entries that break the
    /// rules of Pseudonym.create or repeat are skipped and counted.
    let parsePseudonyms (source: string) =
        let document = Tomlyn.Parsing.SyntaxParser.Parse(source, "pseudonyms", true)
        if document.HasErrors then Error $"Invalid pseudonym TOML: {document.Diagnostics}"
        else
            let table = TomlSerializer.Deserialize<TomlTable>(source)
            match table.Keys |> Seq.tryFind (fun key -> key <> "version" && key <> "names") with
            | Some key -> Error $"Unknown pseudonym setting: {key}"
            | None ->
                match table.TryGetValue "version", table.TryGetValue "names" with
                | (true, (:? int64 as version)), _ when version <> 1L -> Error "Unsupported pseudonym file version."
                | (true, value), _ when not (value :? int64) -> Error "Pseudonym file version must be a number."
                | _, (true, (:? TomlArray as values)) ->
                    let names = values |> Seq.choose (function :? string as text -> Pseudonym.create text |> Result.toOption | _ -> None) |> List.ofSeq
                    match PseudonymDictionary.create names with
                    | ValueNone -> Error "Pseudonym file has no valid names."
                    | ValueSome dictionary -> Ok (dictionary, values.Count - dictionary.Count)
                | _, (true, _) -> Error "Pseudonym names must be an array of strings."
                | _, (false, _) -> Error "Pseudonym file has no names."

    /// Never stops the server: a missing, oversized, broken or empty file
    /// falls back to the built-in list with a warning, like the client does.
    let loadPseudonyms (options: IdentityOptions) : PseudonymDictionary * string option =
        let fallback reason = PseudonymDictionary.builtIn, Some $"{reason}; using the {PseudonymDictionary.builtIn.Count} built-in pseudonyms."
        try
            let file = FileInfo options.PseudonymsPath
            if String.IsNullOrWhiteSpace options.PseudonymsPath || not file.Exists then fallback $"Pseudonym file not found: {options.PseudonymsPath}"
            elif file.Length > MaxPseudonymsBytes then fallback "Pseudonym file exceeds 64 KiB"
            else
                match parsePseudonyms (File.ReadAllText file.FullName) with
                | Error error -> fallback error
                | Ok (dictionary, 0) -> dictionary, None
                | Ok (dictionary, skipped) -> dictionary, Some $"Pseudonym file: {skipped} invalid or repeated entries skipped."
        with
        | :? TomlException as error -> fallback $"Invalid pseudonym TOML: {error.Message}"
        | :? IOException as error -> fallback $"Cannot read pseudonyms: {error.Message}"
        | :? UnauthorizedAccessException as error -> fallback $"Cannot read pseudonyms: {error.Message}"
        | :? ArgumentException as error -> fallback $"Cannot read pseudonyms: {error.Message}"

    let rec private arguments configFile port (remainingArgs: string list) =
        match remainingArgs with
        | [] ->
            let loaded = match configFile with None -> Ok defaults | Some path -> load path
            loaded |> Result.bind validate |> Result.bind (fun config ->
                let updated = match port with None -> config | Some value -> { config with Server = { config.Server with Port = value } }
                validate updated |> Result.map LaunchCommand.Run)
        | "--help" :: _ | "-h" :: _ -> Ok LaunchCommand.Help
        | "--config" :: path :: remaining -> arguments (Some path) port remaining
        | "--port" :: value :: remaining ->
            match UInt16.TryParse value with
            | true, number when number > 0us -> arguments configFile (Some number) remaining
            | true, _ | false, _ -> Error "--port must be between 1 and 65535."
        | ["--write-config"; path] when configFile.IsNone && port.IsNone -> Ok (LaunchCommand.WriteConfig path)
        | unknown :: _ -> Error (sprintf "Unknown or incomplete argument: %s" unknown)

    let parse args = arguments None None (Array.toList args)
