module Dreamsleeve.Server.Tests.Settings

open Dreamsleeve.Server.Core

/// Checked game settings from these sections; invalid ones fail the test here.
let game server runtime identity announcements groundMarks =
    match GameSettings.create server runtime identity announcements groundMarks GuildOptions.defaults with
    | Ok settings -> settings
    | Error errors -> failwithf "Invalid test settings: %A" errors

let defaults =
    game ServerConfig.defaults ServerRuntimeOptions.defaults IdentityOptions.defaults AnnouncementOptions.defaults GroundMarkOptions.defaults

/// The errors GameSettings.create reports; valid settings fail the test.
let errors server runtime identity announcements groundMarks =
    match GameSettings.create server runtime identity announcements groundMarks GuildOptions.defaults with
    | Ok _ -> failwith "Expected invalid settings."
    | Error errors -> errors
