namespace Dreamsleeve.Server

open System
open System.IO
open System.Net
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.Json.Serialization
open Dreamsleeve.Server.Core
open Dreamsleeve.Server.Infrastructure

type ApplicationConfig = {
    Server: ServerConfig
    Runtime: ServerRuntimeOptions
    Profiles: MemoryProfileStoreConfig
}

[<RequireQualifiedAccess>]
type LaunchCommand =
    | Run of ApplicationConfig
    | WriteConfig of string
    | Help

[<RequireQualifiedAccess>]
module Configuration =
    type private AddressConverter() =
        inherit JsonConverter<IPAddress>()
        override _.Read(reader: byref<Utf8JsonReader>, _, _) =
            if reader.TokenType <> JsonTokenType.String then
                raise (JsonException "BindAddress must be an IP address string.")

            match IPAddress.TryParse(reader.GetString()) with
            | true, value -> value
            | false, _ -> raise (JsonException "BindAddress must be an IP address string.")

        override _.Write(writer, value, _) = writer.WriteStringValue(value.ToString())

    let defaults = {
        Server = ServerConfig.defaults
        Runtime = ServerRuntimeOptions.defaults
        Profiles = { MailboxCapacity = 64; MaxPendingReplies = 64 }
    }

    let private jsonOptions () =
        let options = JsonSerializerOptions(WriteIndented = true, PropertyNameCaseInsensitive = true,
                                           UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)
        options.Converters.Add(AddressConverter())
        options

    // A startup file can override a subset of nested settings; omitted values
    // retain their documented defaults. Unknown fields still fail deserialization.
    let rec private merge (target: JsonObject) (patch: JsonObject) =
        for entry in patch do
            let name =
                target |> Seq.tryPick (fun existing ->
                    if String.Equals(existing.Key, entry.Key, StringComparison.OrdinalIgnoreCase) then Some existing.Key else None)
                |> Option.defaultValue entry.Key

            match target[name], entry.Value with
            | (:? JsonObject as nested), (:? JsonObject as updated) -> merge nested updated
            | _, value -> target[name] <- if isNull value then null else value.DeepClone()

    let private load path =
        try
            let options = jsonOptions ()
            let target = JsonSerializer.SerializeToNode(defaults, options).AsObject()
            let patch = JsonNode.Parse(File.ReadAllText path)
            match patch with
            | :? JsonObject as overrides ->
                merge target overrides
                Ok (target.Deserialize<ApplicationConfig>(options))
            | _ -> Error "The configuration root must be a JSON object."
        with
        | :? JsonException as error -> Error (sprintf "Invalid configuration: %s" error.Message)
        | :? ArgumentException as error -> Error (sprintf "Cannot read configuration: %s" error.Message)
        | :? IOException as error -> Error (sprintf "Cannot read configuration: %s" error.Message)
        | :? UnauthorizedAccessException as error -> Error (sprintf "Cannot read configuration: %s" error.Message)

    let writeDefaults path =
        try
            File.WriteAllText(path, JsonSerializer.Serialize(defaults, jsonOptions ()) + Environment.NewLine)
            Ok ()
        with
        | :? ArgumentException as error -> Error error.Message
        | :? IOException as error -> Error error.Message
        | :? UnauthorizedAccessException as error -> Error error.Message

    let private validate (config: ApplicationConfig) =
        if isNull (box config.Server) || isNull (box config.Runtime) || isNull (box config.Profiles)
           || isNull (box config.Server.ChatInput) || isNull (box config.Runtime.Player)
           || isNull (box config.Runtime.Chat) || isNull (box config.Runtime.Presence) then
            Error "Configuration sections cannot be null."
        elif config.Runtime.MaxSessions > config.Server.PeerLimit then
            Error "Runtime.MaxSessions cannot exceed Server.PeerLimit."
        elif config.Runtime.MaxSessions > config.Server.MaxInitialPlayers then
            Error "Server.MaxInitialPlayers must include every admitted session."
        elif config.Runtime.Chat.HistoryCapacity > config.Server.MaxRecentMessages then
            Error "Server.MaxRecentMessages must include the retained chat history."
        elif config.Profiles.MailboxCapacity < 1 || config.Profiles.MaxPendingReplies < 1 then
            Error "Profile mailbox and pending reply capacities must be positive."
        else
            ServerConfig.validate config.Server
            |> Result.map (fun _ -> config)
            |> Result.mapError (String.concat " ")

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
