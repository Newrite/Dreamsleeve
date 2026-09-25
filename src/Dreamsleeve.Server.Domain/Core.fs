namespace Dreamsleeve.Server.Domain

open System

[<RequireQualifiedAccess>]
module Core =

    let toUnixMilliseconds (time: DateTimeOffset) : int64 =
        time.ToUnixTimeMilliseconds()
    
    let fromUnixMilliseconds (value: int64) : DateTimeOffset =
        DateTimeOffset.FromUnixTimeMilliseconds(value)