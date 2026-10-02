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
        [ "openid.ns", "http://specs.openid.net/auth/2.0"; "openid.mode", "checkid_setup"
          "openid.return_to", returnUrl publicUrl flow; "openid.realm", publicUrl.TrimEnd('/')
          "openid.identity", select; "openid.claimed_id", select ]
        |> List.map (fun (key, value) -> key + "=" + Uri.EscapeDataString value)
        |> String.concat "&"
        |> fun query -> Endpoint + "?" + query

    /// The SteamID of the answer the browser brought back for this flow, once
    /// Steam confirms that it issued it. "canceled" when the player turned back.
    /// The flow returns to whichever of the public origins it began through.
    let verify (http: HttpClient) (publicUrls: string list) (flow: string) (fields: (string * string) list) (token: CancellationToken) = task {
        let expected returned = publicUrls |> List.exists (fun publicUrl -> returned = returnUrl publicUrl flow)
        let field name = fields |> List.tryFind (fun (key, _) -> key = name) |> Option.map snd
        match field "openid.mode", field "openid.op_endpoint", field "openid.return_to", field "openid.claimed_id", field "openid.identity" with
        | Some "cancel", _, _, _, _ -> return Error "canceled"
        | Some "id_res", Some Endpoint, Some returned, Some id, Some identity when expected returned && id = identity ->
            let found = claimed.Match id
            if not found.Success then return Error "The claimed ID is not a Steam account."
            else
                // Steam checks its own signature over the very fields it sent.
                let check =
                    fields
                    |> List.filter (fun (key, _) -> key.StartsWith("openid.", StringComparison.Ordinal))
                    |> List.map (fun (key, value) -> KeyValuePair(key, (if key = "openid.mode" then "check_authentication" else value)))
                use content = new FormUrlEncodedContent(check)
                use! response = http.PostAsync(Endpoint, content, token)
                let! body = response.Content.ReadAsStringAsync(token)
                if response.IsSuccessStatusCode && body.Split('\n') |> Array.exists (fun line -> line.Trim() = "is_valid:true") then
                    return Ok(UInt64.Parse found.Groups[1].Value)
                else return Error "Steam did not confirm the sign-in."
        | _ -> return Error "The answer from Steam is incomplete or belongs to another sign-in."
    }

    /// The public profile through the Web API key. Any failure leaves the fields
    /// unknown; nothing about the key reaches a log.
    let profile (http: HttpClient) (key: string) (steamId: uint64) (token: CancellationToken) = task {
        let unknown = { SteamId = steamId; PersonaName = ValueNone; Created = ValueNone }
        try
            use! response = http.GetAsync($"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={Uri.EscapeDataString key}&steamids={steamId}", token)
            if not response.IsSuccessStatusCode then return unknown
            else
                let! text = response.Content.ReadAsStringAsync(token)
                use document = JsonDocument.Parse text
                match document.RootElement.TryGetProperty "response" with
                | true, answer ->
                    match answer.TryGetProperty "players" with
                    | true, players when players.ValueKind = JsonValueKind.Array && players.GetArrayLength() > 0 ->
                        let player = players[0]
                        let name =
                            match player.TryGetProperty "personaname" with
                            | true, value when value.ValueKind = JsonValueKind.String -> ValueSome(value.GetString())
                            | true, _ | false, _ -> ValueNone
                        let created =
                            match player.TryGetProperty "timecreated" with
                            | true, value when value.ValueKind = JsonValueKind.Number -> ValueSome(DateTimeOffset.FromUnixTimeSeconds(value.GetInt64()))
                            | true, _ | false, _ -> ValueNone
                        return { unknown with PersonaName = name; Created = created }
                    | true, _ | false, _ -> return unknown
                | false, _ -> return unknown
        with _ -> return unknown
    }
