module Dreamsleeve.Server.Tests.SteamOpenIdTests

open System
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open Dreamsleeve.Server.Infrastructure
open Expecto
open AgentTests
open BackgroundTests

// Steam as an HttpMessageHandler: it records what it was asked and answers.
type private FakeSteam(answer: HttpRequestMessage -> string) =
    inherit HttpMessageHandler()
    member val Requests = Collections.Concurrent.ConcurrentQueue<string * string>()
    override this.SendAsync(request, _) =
        let body = if isNull request.Content then "" else request.Content.ReadAsStringAsync().Result
        this.Requests.Enqueue((string request.RequestUri, body))
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK, Content = new StringContent(answer request)))

let private publicUrl = "http://127.0.0.1:8779"
let private flow = String('f', 43)

let private answerFields steamId =
    let returnTo = SteamOpenId.returnUrl publicUrl flow
    let claimed = $"https://steamcommunity.com/openid/id/{steamId}"
    [ "openid.ns", "http://specs.openid.net/auth/2.0"; "openid.mode", "id_res"; "openid.op_endpoint", SteamOpenId.Endpoint
      "openid.claimed_id", claimed; "openid.identity", claimed; "openid.return_to", returnTo
      "openid.response_nonce", "2026-10-02T00:00:00Zabc"; "openid.assoc_handle", "1234567890"
      "openid.signed", "signed,op_endpoint,claimed_id,identity,return_to,response_nonce,assoc_handle"; "openid.sig", "c2lnbmF0dXJl" ]

let tests = testList "Steam OpenID" [
    case "the browser goes to Steam with this flow's return address and the public realm" (fun () -> task {
        let url = SteamOpenId.loginUrl (publicUrl + "/") flow
        check (url.StartsWith(SteamOpenId.Endpoint + "?openid.ns=")) url
        let returnTo = Uri.EscapeDataString(publicUrl + "/auth/steam/return?flow=" + flow)
        for part in [ "openid.mode=checkid_setup"; $"openid.return_to={returnTo}"
                      $"openid.realm={Uri.EscapeDataString publicUrl}"; "identifier_select" ] do
            check (url.Contains part) $"Missing {part} in {url}"
    })

    case "an answer is accepted only when Steam confirms it, for this flow and a Steam account" (fun () -> task {
        let confirming = new FakeSteam(fun _ -> "ns:http://specs.openid.net/auth/2.0\nis_valid:true\n")
        use http = new HttpClient(confirming)
        let! accepted = SteamOpenId.verify http [ publicUrl ] flow (answerFields 76561198000000042UL) CancellationToken.None
        equal (Ok 76561198000000042UL) accepted
        let url, body = confirming.Requests.ToArray() |> Array.exactlyOne
        equal SteamOpenId.Endpoint url
        check (body.Contains "openid.mode=check_authentication" && body.Contains "openid.sig=c2lnbmF0dXJl") "Steam checks its own fields."
        let refusing = new FakeSteam(fun _ -> "ns:http://specs.openid.net/auth/2.0\nis_valid:false\n")
        use refused = new HttpClient(refusing)
        let! denied = SteamOpenId.verify refused [ publicUrl ] flow (answerFields 76561198000000042UL) CancellationToken.None
        check (Result.isError denied) "Steam refused it."
        let neverAsked = new FakeSteam(fun _ -> failwith "Steam must not be asked")
        use idle = new HttpClient(neverAsked)
        let swap key value fields = fields |> List.map (fun (k, v) -> if k = key then k, value else k, v)
        for fields in [ answerFields 76561198000000042UL |> swap "openid.return_to" (SteamOpenId.returnUrl publicUrl (String('g', 43)))
                        answerFields 76561198000000042UL |> swap "openid.op_endpoint" "https://evil.example/openid/login"
                        answerFields 76561198000000042UL |> swap "openid.claimed_id" "https://evil.example/openid/id/76561198000000042"
                        answerFields 76561198000000042UL |> swap "openid.identity" "https://steamcommunity.com/openid/id/76561198000000043"
                        answerFields 12345UL ] do
            let! result = SteamOpenId.verify idle [ publicUrl ] flow fields CancellationToken.None
            check (Result.isError result) $"Refused: %A{fields}"
        let! canceled = SteamOpenId.verify idle [ publicUrl ] flow [ "openid.mode", "cancel" ] CancellationToken.None
        equal (Error "canceled") canceled
        // A flow begun through a proxy returns to the proxy's origin.
        let proxied = new FakeSteam(fun _ -> "ns:http://specs.openid.net/auth/2.0\nis_valid:true\n")
        use viaProxy = new HttpClient(proxied)
        let proxyUrl = "https://proxy.example.org"
        let throughProxy = answerFields 76561198000000042UL |> swap "openid.return_to" (SteamOpenId.returnUrl proxyUrl flow)
        let! returned = SteamOpenId.verify viaProxy [ publicUrl; proxyUrl ] flow throughProxy CancellationToken.None
        equal (Ok 76561198000000042UL) returned
        let! unknown = SteamOpenId.verify idle [ publicUrl ] flow throughProxy CancellationToken.None
        check (Result.isError unknown) "An origin the server does not list is refused."
        equal 0 neverAsked.Requests.Count
    })

    case "the profile gives the persona and the creation date when public, nothing otherwise" (fun () -> task {
        let steam = new FakeSteam(fun request ->
            if request.RequestUri.Query.Contains "steamids=76561198000000042" then
                """{"response":{"players":[{"steamid":"76561198000000042","personaname":"Довакин","communityvisibilitystate":3,"timecreated":1420070400}]}}"""
            else """{"response":{"players":[{"steamid":"76561198000000043","personaname":"Тихий","communityvisibilitystate":1}]}}""")
        use http = new HttpClient(steam)
        let! visible = SteamOpenId.profile http "KEY" 76561198000000042UL CancellationToken.None
        equal (ValueSome "Довакин", ValueSome (DateTimeOffset.FromUnixTimeSeconds 1420070400L)) (visible.PersonaName, visible.Created)
        let! hidden = SteamOpenId.profile http "KEY" 76561198000000043UL CancellationToken.None
        equal (ValueSome "Тихий", ValueNone) (hidden.PersonaName, hidden.Created)
        let broken = new FakeSteam(fun _ -> "not json")
        use failing = new HttpClient(broken)
        let! unknown = SteamOpenId.profile failing "KEY" 76561198000000042UL CancellationToken.None
        equal (ValueNone, ValueNone) (unknown.PersonaName, unknown.Created)
    })
]
