module Dreamsleeve.Server.Tests.Packets

open System
open Dreamsleeve.Server.Core

/// The one packet that carries a response when nothing limits its size;
/// movement then stays a single batch too.
let single codec response =
    ProtocolCodec.encode codec Int32.MaxValue response |> Result.map List.exactlyOne
