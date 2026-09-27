module Dreamsleeve.Server.NetworkBenchmarks.Movement

open System
open System.Collections.Generic
open Dreamsleeve.Protocol.Chat
open Dreamsleeve.Server.NetworkBenchmarks.Measurements

// One owner, no actors/locks. All clocks are the generator's monotonic clock.
type Probe(scenario: string, rate: float, ids: uint64 array, now: unit -> float,
           send: int -> ClientPacket -> bool, fail: string -> unit) =
    let count = ids.Length
    let indices = ids |> Array.mapi (fun index id -> id, index) |> dict
    let pending = Array.init count (fun _ -> Dictionary<uint64, float * bool>())
    let nextRequest = Array.create count 2UL
    let due = Array.zeroCreate<float> count
    let latest = Array.zeroCreate<PlayerLocation> count
    let seen = Array2D.zeroCreate<uint64> count count
    let seenX = Array2D.zeroCreate<float32> count count
    let initialized = Array2D.zeroCreate<bool> count count
    let mutable initializedCount = 0
    let ages, acknowledgements = Distribution(), Distribution()
    let mutable measuring = false
    let mutable started = 0.
    let mutable sent = 0L
    let mutable received = 0L
    let mutable cleared = 0L
    let mutable sentBytes = 0L
    let mutable receivedBytes = 0L
    let mutable missed = 0L
    let mutable throttled = 0L
    let mutable maxPending = 0
    let mutable finalConverged = false

    let submit index action =
        let requestId = nextRequest[index]
        nextRequest[index] <- requestId + 1UL
        let packet = ClientPacket(ProtocolVersion = Dreamsleeve.Server.Core.ProtocolCodec.Version,
                                  RequestId = requestId, UpdatePlayer = action)
        if send index packet then
            pending[index].Add(requestId, (now(), measuring))
            maxPending <- max maxPending pending[index].Count
            if measuring then
                sent <- sent + 1L
                sentBytes <- sentBytes + int64 (packet.CalculateSize())
            true
        else false

    let position index time =
        let step = int ((time - started) / 2000.)
        let group, offset =
            match scenario with
            | "dense" -> 0, 0.f
            | "spaces" -> index % 10, 0.f
            | "sparse" -> 0, float32 (index / 25) * 20000.f
            | "boundaries" ->
                // Independent groups alternate space and jump across the radius.
                (if (step + index / 25) % 3 = 2 then 1 else 0),
                (if (step + index / 25) % 2 = 0 then 0.f else 12000.f)
            | _ -> invalidArg "scenario" "Unknown movement scenario"
        let x = offset + float32 (index % 25) * 4.f + 100.f * float32 (sin ((time - started) / 500.))
        PlayerLocation(
            Location = Location(LocationId = FormKey(PluginName = "skyrim.esm", LocalFormId = uint32 (0x3c + group)), LocationName = "Benchmark"),
            Position = Position(X = x, Y = 0.f, Z = 0.f), Rotation = Rotation(Z = float32 ((time - started) / 1000.)),
            SampledAtUs = uint64 (time * 1000.) + 1UL)

    let move index time =
        let location = position index time
        if submit index (UpdatePlayer(SampleMovement = SampleMovement(Location = location))) then
            latest[index] <- location

    let observe observer source (location: PlayerLocation) =
        match indices.TryGetValue source with
        | false, _ -> fail "Movement from unknown player"
        | true, index ->
            if isNull location then
                seen[observer, index] <- 0UL
                if measuring then cleared <- cleared + 1L
            elif isNull location.Position || isNull location.Location || isNull location.Location.LocationId || location.SampledAtUs = 0UL then
                fail "Invalid measured movement"
            else
                if location.SampledAtUs < seen[observer, index] then fail "Movement reordered within a visible stream"
                seen[observer, index] <- location.SampledAtUs
                seenX[observer, index] <- location.Position.X
                if measuring then
                    received <- received + 1L
                    ages.Add(max 0. (now() - float (location.SampledAtUs - 1UL) / 1000.))

    member _.BeginCharacters(serviceBatch: unit -> unit) =
        for index in 0 .. count - 1 do
            submit index (UpdatePlayer(BeginCharacter = BeginCharacter(Name = "Benchmark"))) |> ignore
            if index % 16 = 15 then serviceBatch()

    member _.Prepared = initializedCount = count * count && (pending |> Array.forall (fun requests -> requests.Count = 0))

    member _.Pending = pending |> Array.sumBy _.Count

    member _.Start() =
        measuring <- true
        started <- now()
        for index in 0 .. count - 1 do due[index] <- started + float index * 1000. / (rate * float count)

    member _.SendDue() =
        let time = now()
        let interval = 1000. / rate
        for index in 0 .. count - 1 do
            if time >= due[index] then
                // Never hide overload behind a catch-up burst or an unbounded queue.
                missed <- missed + int64 (floor ((time - due[index]) / interval))
                due[index] <- time + interval
                if pending[index].Count >= 8 then throttled <- throttled + 1L
                else move index time

    member _.Stop() = measuring <- false

    member _.FinalSamples(pump: unit -> unit) =
        for index in 0 .. count - 1 do
            move index (now())
            if index % 16 = 15 then pump()

    member _.Receive(observer, packet: ServerPacket, size) =
        if measuring then receivedBytes <- receivedBytes + int64 size
        match packet.PayloadCase with
        | ServerPacket.PayloadOneofCase.PlayerUpdateAccepted ->
            match pending[observer].TryGetValue packet.RequestId with
            | true, (sentAt, measured) ->
                pending[observer].Remove packet.RequestId |> ignore
                if measured then acknowledgements.Add(now() - sentAt)
            | false, _ -> fail "Unmatched movement acknowledgement"
        | ServerPacket.PayloadOneofCase.PlayersMoved ->
            for item in packet.PlayersMoved.Players do observe observer item.PlayerId item.Location
        | ServerPacket.PayloadOneofCase.PlayerUpdated ->
            let player = packet.PlayerUpdated.Player
            if isNull player || isNull player.Profile then fail "Invalid player update"
            else
                match indices.TryGetValue player.Profile.PlayerId with
                | true, index when player.CharacterGeneration = 1UL && player.HasCharacterName && player.CharacterName = "Benchmark" ->
                    if not initialized[observer, index] then
                        initialized[observer, index] <- true
                        initializedCount <- initializedCount + 1
                | _ -> fail "Unexpected character initialization"
                observe observer player.Profile.PlayerId player.Location
        | ServerPacket.PayloadOneofCase.PlayerMetadataChanged -> ()
        | _ -> fail "Unexpected movement response"

    member _.Converged() =
        let mutable valid = pending |> Array.forall (fun requests -> requests.Count = 0)
        for observer in 0 .. count - 1 do
            for source in 0 .. count - 1 do
                let origin, target = latest[observer], latest[source]
                if isNull origin || isNull target then valid <- false
                else
                    let dx = double origin.Position.X - double target.Position.X
                    let visible = observer = source || (origin.Location.LocationId = target.Location.LocationId && dx * dx <= 8192. * 8192.)
                    if visible then
                        if seen[observer, source] <> target.SampledAtUs || seenX[observer, source] <> target.Position.X then valid <- false
                    elif seen[observer, source] <> 0UL then valid <- false
        finalConverged <- valid
        valid

    member _.Report(loadMs) =
        {| scenario = scenario; clients = count; sourceHz = rate; replicationMs = 100; visibilityDistance = 8192
           sentSamples = sent; receivedMovements = received; visibilityClears = cleared
           sentPayloadBytes = sentBytes; receivedPayloadBytes = receivedBytes
           sentPayloadBytesPerSecond = float sentBytes * 1000. / max 1. loadMs
           receivedPayloadBytesPerSecond = float receivedBytes * 1000. / max 1. loadMs
           actualSamplesPerClientSecond = float sent * 1000. / (max 1. loadMs * float count)
           generatorMissedIntervals = missed; pendingThrottledSamples = throttled; maxPendingPerClient = maxPending
           initializedPairs = initializedCount; expectedInitializedPairs = count * count
           finalStateConverged = finalConverged; pending = pending |> Array.sumBy _.Count
           deliveryAgeMs = ages.Summary(); ackMs = acknowledgements.Summary()
           percentileMethod = "fixed logarithmic histogram, upper bounds within 1% + 0.01 ms" |}
