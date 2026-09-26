namespace Dreamsleeve.Server.Domain

open System

[<RequireQualifiedAccess>]
module Core =

    let toUnixMilliseconds (time: DateTimeOffset) : int64 =
        time.ToUnixTimeMilliseconds()
    
    /// Converts a trusted timestamp in DateTimeOffset's supported range.
    /// Like the BCL method, this throws ArgumentOutOfRangeException outside that range;
    /// a future decoder must handle invalid wire timestamps at its input boundary.
    let fromUnixMilliseconds (value: int64) : DateTimeOffset =
        DateTimeOffset.FromUnixTimeMilliseconds(value)
