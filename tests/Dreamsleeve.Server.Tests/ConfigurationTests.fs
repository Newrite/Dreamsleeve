module Dreamsleeve.Server.Tests.ConfigurationTests

open System
open System.IO
open Expecto
open Dreamsleeve.Server

let private withFile (text: string) action =
    let path = Path.Combine(Path.GetTempPath(), sprintf "dreamsleeve-config-%O.json" (Guid.NewGuid()))
    try
        File.WriteAllText(path, text)
        action path
    finally
        File.Delete path

let tests = testList "Server configuration" [
    testCase "partial nested settings retain defaults and CLI port takes precedence" <| fun _ ->
        withFile "{\"server\":{\"Port\":9000},\"Runtime\":{\"Player\":{\"MaxPendingChat\":3}}}" (fun path ->
            match Configuration.parse [|"--config"; path; "--port"; "9001"|] with
            | Ok (LaunchCommand.Run config) ->
                Expect.equal config.Server.Port 9001us "CLI override"
                Expect.equal config.Runtime.Player.MaxPendingChat 3 "nested override"
                Expect.equal config.Server.MaxOutgoingBytes Configuration.defaults.Server.MaxOutgoingBytes "omitted budget preserved"
            | other -> failwithf "Expected valid config: %A" other)

    testCase "unknown properties, wrong types and null sections are rejected" <| fun _ ->
        for source in ["{\"Server\":{\"Typo\":1}}"; "{\"Runtime\":null}"; "{\"Server\":null}"; "{\"Server\":{\"BindAddress\":42}}"; "[]"] do
            withFile source (fun path ->
                Expect.isError (Configuration.parse [|"--config"; path|]) "invalid config must fail"
                Expect.isError (Configuration.parse [|"--config"; path; "--port"; "9001"|]) "CLI override cannot bypass null validation")

    testCase "cross-owner limits cannot make welcome exceed configured collection bounds" <| fun _ ->
        for source in ["{\"Server\":{\"MaxInitialPlayers\":1}}"; "{\"Server\":{\"MaxRecentMessages\":1}}"] do
            withFile source (fun path ->
                Expect.isError (Configuration.parse [|"--config"; path|]) "incompatible limits rejected")

    testCase "exported defaults load without losing any nested settings" <| fun _ ->
        withFile "{}" (fun path ->
            Configuration.writeDefaults path |> function Ok () -> () | Error error -> failwith error
            match Configuration.parse [|"--config"; path|] with
            | Ok (LaunchCommand.Run config) -> Expect.equal config Configuration.defaults "roundtrip all fields"
            | other -> failwithf "Cannot load exported defaults: %A" other)
    testCase "authentication requires TLS outside explicitly enabled literal loopback" <| fun _ ->
        for source in [
            """{"Authentication":{"ListenUrl":"http://0.0.0.0:8779"}}"""
            """{"Authentication":{"ListenUrl":"http://localhost:8779"}}"""
            """{"Authentication":{"AllowInsecureLoopback":false}}"""
            """{"Authentication":{"ListenUrl":"https://user:secret@example.com"}}"""
            """{"Authentication":{"ListenUrl":"https://example.com/auth"}}"""
            """{"Authentication":{"ListenUrl":null}}"""
        ] do
            withFile source (fun path ->
                Expect.isError (Configuration.parse [|"--config"; path|]) "unsafe or ambiguous binding rejected")
        withFile """{"Authentication":{"ListenUrl":"https://example.com:9443","AllowInsecureLoopback":false}}""" (fun path ->
            Expect.isOk (Configuration.parse [|"--config"; path|]) "TLS endpoint accepted")

    testCase "account and logging limits and null settings fail before startup" <| fun _ ->
        for source in [
            """{"Authentication":null}"""; """{"Database":null}"""; """{"Logging":null}"""
            """{"Authentication":{"Service":null}}"""
            """{"Authentication":{"Service":{"MaxConcurrentOperations":0}}}"""
            """{"Authentication":{"Service":{"PasswordIterations":1}}}"""
            """{"Authentication":{"RequestsPerMinute":0}}"""
            """{"Database":{"DatabasePath":""}}"""
            """{"Logging":{"MinimumLevel":"bogus"}}"""
            """{"Logging":{"Console":false,"FilePath":""}}"""
        ] do
            withFile source (fun path ->
                Expect.isError (Configuration.parse [|"--config"; path|]) "invalid settings rejected")
]
