namespace Dreamsleeve.Server

open System
open System.IO
open System.Net
open System.Globalization
open Microsoft.FSharp.Reflection
open Tomlyn
open Tomlyn.Model
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Infrastructure

type AuthenticationSettings = {
    ListenUrl: string
    AllowInsecureLoopback: bool
    AllowRegistration: bool
    CertificatePath: string
    RequestsPerMinute: int
    RequestTimeoutSeconds: int
    Service: AccountServiceOptions
}

type ApplicationConfig = {
    Server: ServerConfig
    Runtime: ServerRuntimeOptions
    Database: SqliteAccountStoreConfig
    Authentication: AuthenticationSettings
    Logging: LoggingSettings
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
            ListenUrl = "http://127.0.0.1:8779"; AllowInsecureLoopback = true; AllowRegistration = true
            CertificatePath = ""; RequestsPerMinute = 120; RequestTimeoutSeconds = 15
            Service = AuthService.defaults
        }
        Logging = ServerLogging.defaults
    }

    // Records remain immutable domain settings. TOML overrides only supplied fields.
    let rec private overlay path (current: obj) (input: obj) : Result<obj, string> =
        let target = current.GetType()
        let invalid () = Error $"Invalid TOML value or type: {path}"
        if FSharpType.IsRecord target then
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
        if FSharpType.IsRecord valueType then
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

    let private validate (config: ApplicationConfig) =
        if isNull (box config.Server) || isNull (box config.Runtime) || isNull (box config.Database) || isNull (box config.Authentication) || isNull (box config.Logging)
           || isNull (box config.Server.ChatInput) || isNull (box config.Server.PlayerInput) || isNull (box config.Runtime.Player)
           || isNull (box config.Runtime.Chat) || isNull (box config.Runtime.Presence)
           || isNull (box config.Authentication.Service) then
            Error "Configuration sections cannot be null."
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
        elif not (AuthService.validate config.Authentication.Service).IsEmpty then
            Error (String.concat " " (AuthService.validate config.Authentication.Service))
        else
            match Uri.TryCreate(config.Authentication.ListenUrl, UriKind.Absolute) with
            | false, _ -> Error "Authentication.ListenUrl must be an absolute HTTP(S) URL."
            | true, uri when not (String.IsNullOrEmpty uri.UserInfo) || uri.AbsolutePath <> "/"
                             || not (String.IsNullOrEmpty uri.Query) || not (String.IsNullOrEmpty uri.Fragment) ->
                Error "Authentication URL must contain only scheme, host and port."
            | true, uri when uri.Scheme <> "http" && uri.Scheme <> "https" ->
                Error "Authentication requires HTTP(S)."
            | true, uri when uri.Scheme = "http" && not (config.Authentication.AllowInsecureLoopback
                                  && (uri.Host = "127.0.0.1" || uri.Host = "[::1]" || uri.Host = "::1")) ->
                Error "Unencrypted authentication is allowed only on an explicitly enabled literal loopback address."
            | true, _ ->
                ServerLogging.validate config.Logging
                |> Result.bind (fun () -> ServerConfig.validate config.Server |> Result.mapError (String.concat " "))
                |> Result.map (fun _ -> config)

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
