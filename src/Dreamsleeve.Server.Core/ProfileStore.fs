namespace Dreamsleeve.Server.Core

open System
open Dreamsleeve.Agent
open Dreamsleeve.Server.Domain

[<RequireQualifiedAccess>]
type ProfileStoreError =
    | UsernameTaken
    | IdExhausted
    | Canceled
    | Failed of exn

[<RequireQualifiedAccess>]
type ProfileCommand =
    | FindByUsername of Username
    | Create of Username * DisplayName
    /// Atomically return the existing profile or create one; never change an existing profile.
    | GetOrCreate of Username * DisplayName

[<RequireQualifiedAccess>]
type ProfileOutcome =
    | Found of PlayerData option
    | Created of PlayerData
    | Resolved of PlayerData

type ProfileReply = {
    OperationId: Guid
    Result: Result<ProfileOutcome, ProfileStoreError>
}

type ProfileRequest = {
    OperationId: Guid
    Command: ProfileCommand
    ReplyTo: ReliableAgentRef<ProfileReply>
}
