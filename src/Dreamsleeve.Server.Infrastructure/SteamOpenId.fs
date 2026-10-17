namespace Dreamsleeve.Server.Infrastructure

open System
open System.Collections.Generic
open System.Net.Http
open System.Text.Json
open System.Text.RegularExpressions
open System.Threading

/// What Steam says about an account: the SteamID, vouched for through OpenID,
/// and with a Web API key its public profile.
type SteamProfile = {
    SteamId: uint64
    PersonaName: string voption
    /// Steam returns it only for a public profile.
    Created: DateTimeOffset voption
}

[<RequireQualifiedAccess>]
type SteamRequestError =
    | HttpStatus of int
    | Transport of HttpRequestException
    | RequestCanceled
    | TimedOut

[<RequireQualifiedAccess>]
type SteamVerifyError =
    | UserCanceled
    | InvalidAnswer
    | NotConfirmed
    | Request of SteamRequestError

[<RequireQualifiedAccess>]
type SteamProfileFormatError =
    | Json
    | Envelope
    | Identity
    | Name
    | Timestamp

[<RequireQualifiedAccess>]
type SteamProfileError =
    | InvalidResponse of SteamProfileFormatError
    | Request of SteamRequestError

/// "Sign in through Steam" (OpenID 2.0, docs/AuthenticationRu.md, «Вход через
/// Steam»): the browser goes to Steam and comes back with a claimed ID that the
/// server has Steam confirm (check_authentication). Steam needs no registration
/// of the site; the Web API key only adds the profile.
[<RequireQualifiedAccess>]
module SteamOpenId =
    [<Literal>]
    let Endpoint = "https://steamcommunity.com/openid/login"

    let private claimed = Regex(@"^https://steamcommunity\.com/openid/id/(7656119\d{10})$", RegexOptions.CultureInvariant)

    /// Where Steam sends the browser back for this flow.
    let returnUrl (publicUrl: string) (flow: string) = $"{publicUrl.TrimEnd('/')}/auth/steam/return?flow={Uri.EscapeDataString flow}"

    /// The Steam page the browser opens for this flow.
    let loginUrl (publicUrl: string) (flow: string) =
        let select = "http://specs.openid.net/auth/2.0/identifier_select"
        [
            "openid.ns", "http://specs.openid.net/auth/2.0"
            "openid.mode", "checkid_setup"
            "openid.return_to", returnUrl publicUrl flow
            "openid.realm", publicUrl.TrimEnd('/')
            "openid.identity", select
            "openid.claimed_id", select
        ]
        |> List.map (fun (key, value) -> key + "=" + Uri.EscapeDataString value)
        |> String.concat "&"
        |> fun query -> Endpoint + "?" + query

    let private send (http: HttpClient) (request: HttpRequestMessage) (token: CancellationToken) = task {
        try
            let! response = http.SendAsync(request, HttpCompletionOption.ResponseContentRead, token)
            return Ok response
        with
        | :? HttpRequestException as error -> return Error (SteamRequestError.Transport error)
        | :? OperationCanceledException ->
            return Error (if token.IsCancellationRequested then SteamRequestError.RequestCanceled else SteamRequestError.TimedOut)
    }

    let private readContent (content: HttpContent) (token: CancellationToken) = task {
        try
            let! text = content.ReadAsStringAsync(token)
            return Ok text
        with
        | :? HttpRequestException as error -> return Error (SteamRequestError.Transport error)
        | :? OperationCanceledException ->
            return Error (if token.IsCancellationRequested then SteamRequestError.RequestCanceled else SteamRequestError.TimedOut)
    }

    let private readHttp http request token = task {
        match! send http request token with
        | Error error -> return Error error
        | Ok received ->
            use response = received
            if not response.IsSuccessStatusCode then
                return Error (SteamRequestError.HttpStatus (int response.StatusCode))
            else
                return! readContent response.Content token
    }

    /// The SteamID confirmed for this flow; an intentional browser cancellation
    /// is distinct from invalid answers and dependency failures.
    let verify (http: HttpClient) (publicUrls: string list) (flow: string) (fields: (string * string) list) (token: CancellationToken) = task {
        let expected returned = publicUrls |> List.exists (fun publicUrl -> returned = returnUrl publicUrl flow)
        let field name = fields |> List.tryFind (fun (key, _) -> key = name) |> Option.map snd
        match field "openid.mode", field "openid.op_endpoint", field "openid.return_to", field "openid.claimed_id", field "openid.identity" with
        | Some "cancel", _, _, _, _ -> return Error SteamVerifyError.UserCanceled
        | Some "id_res", Some Endpoint, Some returned, Some id, Some identity when expected returned && id = identity ->
            let found = claimed.Match id
            let validNumber, steamId = UInt64.TryParse(found.Groups[1].Value, Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture)
            if not found.Success || not validNumber then
                return Error SteamVerifyError.InvalidAnswer
            else
                // Steam checks its own signature over the very fields it sent.
                let check =
                    fields
                    |> List.filter (fun (key, _) -> key.StartsWith("openid.", StringComparison.Ordinal))
                    |> List.map (fun (key, value) -> KeyValuePair(key, (if key = "openid.mode" then "check_authentication" else value)))
                use content = new FormUrlEncodedContent(check)
                use request = new HttpRequestMessage(HttpMethod.Post, Endpoint, Content = content)
                match! readHttp http request token with
                | Error error -> return Error (SteamVerifyError.Request error)
                | Ok body when body.Split('\n') |> Array.exists (fun line -> line.Trim() = "is_valid:true") ->
                    return Ok steamId
                | Ok _ -> return Error SteamVerifyError.NotConfirmed
        | _ -> return Error SteamVerifyError.InvalidAnswer
    }

    let private parseProfile (text: string) =
        try
            Ok (JsonDocument.Parse text)
        with :? JsonException -> Error SteamProfileFormatError.Json

    let private profileFields steamId (root: JsonElement) =
        let unknown = {
            SteamId = steamId
            PersonaName = ValueNone
            Created = ValueNone
        }

        let decode () =
            if root.ValueKind <> JsonValueKind.Object then
                Error SteamProfileFormatError.Envelope
            else
                match root.TryGetProperty "response" with
                | true, response when response.ValueKind = JsonValueKind.Object ->
                    match response.TryGetProperty "players" with
                    | true, players when players.ValueKind = JsonValueKind.Array ->
                        if players.GetArrayLength() = 0 then
                            Ok unknown
                        else
                            let player = players[0]
                            if player.ValueKind <> JsonValueKind.Object then
                                Error SteamProfileFormatError.Envelope
                            else
                                let identity =
                                    match player.TryGetProperty "steamid" with
                                    | true, value when value.ValueKind = JsonValueKind.String ->
                                        match UInt64.TryParse(value.GetString(), Globalization.NumberStyles.None, Globalization.CultureInfo.InvariantCulture) with
                                        | true, actual when actual = steamId -> Ok ()
                                        | _ -> Error SteamProfileFormatError.Identity
                                    | _ -> Error SteamProfileFormatError.Identity

                                let name () =
                                    match player.TryGetProperty "personaname" with
                                    | false, _ -> Ok ValueNone
                                    | true, value when value.ValueKind = JsonValueKind.String -> Ok (ValueSome (value.GetString()))
                                    | true, _ -> Error SteamProfileFormatError.Name

                                let created () =
                                    match player.TryGetProperty "timecreated" with
                                    | false, _ -> Ok ValueNone
                                    | true, value when value.ValueKind = JsonValueKind.Number ->
                                        match value.TryGetInt64() with
                                        | true, seconds when seconds >= DateTimeOffset.MinValue.ToUnixTimeSeconds() && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds() ->
                                            Ok (ValueSome (DateTimeOffset.FromUnixTimeSeconds seconds))
                                        | _ -> Error SteamProfileFormatError.Timestamp
                                    | true, _ -> Error SteamProfileFormatError.Timestamp

                                match identity with
                                | Error error -> Error error
                                | Ok () ->
                                    match name (), created () with
                                    | Ok name, Ok created ->
                                        Ok {
                                            unknown with
                                                PersonaName = name
                                                Created = created
                                        }
                                    | Error error, _ | _, Error error -> Error error
                    | _ -> Error SteamProfileFormatError.Envelope
                | _ -> Error SteamProfileFormatError.Envelope
        // JsonDocument defers Unicode unescape until member access; only JSON
        // representation reads and total scalar conversions run in this adapter.
        try decode ()
        with :? InvalidOperationException -> Error SteamProfileFormatError.Json

    /// The optional public profile. Expected dependency/representation errors
    /// stay typed; the caller owns fallback without exposing the API key in logs.
    let profile (http: HttpClient) (key: string) (steamId: uint64) (token: CancellationToken) = task {
        use request = new HttpRequestMessage(HttpMethod.Get, $"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={Uri.EscapeDataString key}&steamids={steamId}")
        match! readHttp http request token with
        | Error error -> return Error (SteamProfileError.Request error)
        | Ok text ->
            match parseProfile text with
            | Error error -> return Error (SteamProfileError.InvalidResponse error)
            | Ok parsed ->
                use document = parsed
                return profileFields steamId document.RootElement |> Result.mapError SteamProfileError.InvalidResponse
    }
