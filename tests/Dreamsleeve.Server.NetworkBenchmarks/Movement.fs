module Dreamsleeve.Server.NetworkBenchmarks.Movement

open System
open System.Collections.Generic
open Google.Protobuf
open Dreamsleeve.Protocol.Chat
open Dreamsleeve.Server.NetworkBenchmarks.Measurements

// One owner, no actors/locks. All clocks are the generator's monotonic clock.
type Probe(scenario: string, rate: float, replicationMs: int, ids: uint64 array, offset: int, stride: int, count: int, now: unit -> float,
           send: int -> ClientPacket -> bool, sendMovement: int -> ClientMovementPacket -> bool, fail: string -> unit) =
    let total = ids.Length
    let globalIndex index = offset + index * stride
    let indices = ids |> Array.mapi (fun index id -> id, index) |> dict
    let pending = Array.init count (fun _ -> Dictionary<uint64, float * bool>())
    let nextRequest = Array.create count 2UL
    let due = Array.zeroCreate<float> count
    let latest = Array.zeroCreate<PlayerLocation> total
    let contexts = Array.zeroCreate<uint64> count
    let sequences = Array.zeroCreate<uint64> count
    let revisions = Array2D.zeroCreate<uint64> count total
    let receivedSequences = Array2D.zeroCreate<uint64> count total
    let visible = Array2D.zeroCreate<bool> count total
    let seen = Array2D.zeroCreate<uint64> count total
    let lastReceive = Array2D.create count total -1.
    let receiveGaps = Distribution()
    let seenSpaces = Array2D.zeroCreate<FormKey> count total
    let mutable warmPositions: PlayerLocation array = [||]
    let seenX = Array2D.zeroCreate<float32> count total
    let initialized = Array2D.zeroCreate<bool> count total
    let mutable initializedCount = 0
    let ages, acknowledgements, iterations = Distribution(), Distribution(), Distribution()
    let mutable crossProcess = 0L
    let mutable measuring = false
    let mutable started = 0.
    let mutable sent = 0L
    let mutable received = 0L
    let mutable cleared = 0L
    let mutable sentBytes = 0L
    let mutable receivedBytes = 0L
    let mutable missed = 0L
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

    let publishPose index (location: PlayerLocation) =
        sequences[index] <- sequences[index] + 1UL
        let packet = ClientMovementPacket(
            ProtocolVersion = Dreamsleeve.Server.Core.ProtocolCodec.Version,
            Sample = MovementSample(ContextRevision = contexts[index], Sequence = sequences[index],
                                    Pose = MovementPose(Position = location.Position, Rotation = location.Rotation,
                                                        SampledAtUs = location.SampledAtUs)))
        if sendMovement index packet && measuring then
            sent <- sent + 1L
            sentBytes <- sentBytes + int64 (packet.CalculateSize())

    let move index time =
        let source = globalIndex index
        let location = position source time
        if isNull latest[source] || latest[source].Location <> location.Location then
            contexts[index] <- contexts[index] + 1UL
            sequences[index] <- 0UL
            submit index (UpdatePlayer(SetLocation = SetPlayerLocation(ContextRevision = contexts[index], Location = location))) |> ignore
        latest[source] <- location
        publishPose index location

    let observe observer index (pose: MovementPose) =
        if isNull pose || isNull pose.Position || isNull pose.Rotation || pose.SampledAtUs = 0UL then
            fail "Invalid measured movement"
        else
            let fresh = seen[observer, index] <> pose.SampledAtUs
            seen[observer, index] <- pose.SampledAtUs
            seenX[observer, index] <- pose.Position.X
            if measuring && fresh then
                let receivedAt = now()
                let previous = lastReceive[observer, index]
                if previous >= started then receiveGaps.Add(max 0. (receivedAt - previous))
                lastReceive[observer, index] <- receivedAt
                received <- received + 1L
                if index % stride <> offset then crossProcess <- crossProcess + 1L
                ages.Add(max 0. (receivedAt - float (pose.SampledAtUs - 1UL) / 1000.))

    let baseline observer source revision sequence (location: PlayerLocation) =
        match indices.TryGetValue source with
        | false, _ -> fail "Visibility from unknown player"
        | true, index ->
            if revision > revisions[observer, index]
               || (revision > 0UL && revision = revisions[observer, index] && sequence >= receivedSequences[observer, index]) then
                revisions[observer, index] <- revision
                receivedSequences[observer, index] <- sequence
                visible[observer, index] <- not (isNull location)
                if isNull location then
                    seen[observer, index] <- 0UL
                    lastReceive[observer, index] <- -1.
                    if measuring then cleared <- cleared + 1L
                else
                    seenSpaces[observer, index] <- location.Location.LocationId
                    observe observer index (MovementPose(Position = location.Position, Rotation = location.Rotation, SampledAtUs = location.SampledAtUs))

    member _.BeginCharacters(serviceBatch: unit -> unit) =
        for index in 0 .. count - 1 do
            submit index (UpdatePlayer(BeginCharacter = BeginCharacter(Name = "Benchmark"))) |> ignore
            if index % 16 = 15 then serviceBatch()

    member _.PreparePositions(serviceBatch: unit -> unit) =
        started <- now()
        warmPositions <- Array.init total (fun index -> position index started)
        for index in 0 .. count - 1 do
            move index started
            if index % 16 = 15 then serviceBatch()

    member _.PositionsPrepared =
        let mutable ready = warmPositions.Length = total && (pending |> Array.forall (fun requests -> requests.Count = 0))
        if ready then
            for observer in 0 .. count - 1 do
                let origin = warmPositions[globalIndex observer]
                for source in 0 .. total - 1 do
                    let target = warmPositions[source]
                    let dx = double origin.Position.X - double target.Position.X
                    let expected = globalIndex observer = source ||
                                   (origin.Location.LocationId = target.Location.LocationId && dx * dx <= 8192. * 8192.)
                    if expected then
                        if not visible[observer, source] || seenX[observer, source] <> target.Position.X
                           || seenSpaces[observer, source] <> target.Location.LocationId then ready <- false
                    elif visible[observer, source] then ready <- false
        ready

    member _.Prepared = initializedCount = count * total && (pending |> Array.forall (fun requests -> requests.Count = 0))

    member _.Pending = pending |> Array.sumBy _.Count

    member _.Start(startTime) =
        measuring <- true
        started <- startTime
        for index in 0 .. count - 1 do due[index] <- started + float (globalIndex index) * 1000. / (rate * float total)

    member _.SendDue() =
        let time = now()
        let interval = 1000. / rate
        for index in 0 .. count - 1 do
            if time >= due[index] then
                // Never hide overload behind a catch-up burst or an unbounded queue.
                missed <- missed + int64 (floor ((time - due[index]) / interval))
                due[index] <- time + interval
                move index time

    member _.RecordIteration(duration) = iterations.Add duration

    member _.Stop() = measuring <- false

    member _.FinalSamples(pump: unit -> unit) =
        for index in 0 .. count - 1 do
            move index (now())
            if index % 16 = 15 then pump()

    member _.RepeatFinalSamples() =
        let time = now()
        for index in 0 .. count - 1 do
            if time >= due[index] && not (isNull latest[globalIndex index]) then
                due[index] <- time + 1000. / rate
                publishPose index latest[globalIndex index]

    member _.ReceiveMovement(observer, packet: ServerMovementPacket, size) =
        if measuring then receivedBytes <- receivedBytes + int64 size
        if isNull packet.Movements then fail "Missing movement payload"
        else
            for item in packet.Movements.Players do
                match indices.TryGetValue item.PlayerId with
                | false, _ -> fail "Movement from unknown player"
                | true, index ->
                    if visible[observer, index] && item.ViewRevision = revisions[observer, index]
                       && item.Sequence > receivedSequences[observer, index] then
                        receivedSequences[observer, index] <- item.Sequence
                        observe observer index item.Pose

    member _.Receive(observer, packet: ServerPacket, size) =
        if measuring then receivedBytes <- receivedBytes + int64 size
        match packet.PayloadCase with
        | ServerPacket.PayloadOneofCase.PlayerUpdateAccepted ->
            match pending[observer].TryGetValue packet.RequestId with
            | true, (sentAt, measured) ->
                pending[observer].Remove packet.RequestId |> ignore
                if measured then acknowledgements.Add(now() - sentAt)
            | false, _ -> fail "Unmatched control acknowledgement"
        | ServerPacket.PayloadOneofCase.PlayerVisibilityChanged ->
            let value = packet.PlayerVisibilityChanged
            baseline observer value.PlayerId value.ViewRevision value.Sequence value.Location
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
                baseline observer player.Profile.PlayerId player.ViewRevision player.MovementSequence player.Location
        | ServerPacket.PayloadOneofCase.PlayerMetadataChanged -> ()
        | _ -> fail "Unexpected movement response"

    member _.FinalLocations =
        Array.init count (fun index -> latest[globalIndex index]) |> Array.map (fun location -> Convert.ToBase64String(location.ToByteArray()))

    member _.SetFinalLocations(locations: string array) =
        if locations.Length <> total then invalidOp "Incomplete final location manifest"
        for index in 0 .. total - 1 do
            latest[index] <- PlayerLocation.Parser.ParseFrom(Convert.FromBase64String locations[index])

    member _.Converged() =
        let mutable valid = pending |> Array.forall (fun requests -> requests.Count = 0)
        for observer in 0 .. count - 1 do
            for source in 0 .. total - 1 do
                let origin, target = latest[globalIndex observer], latest[source]
                if isNull origin || isNull target then valid <- false
                else
                    let dx = double origin.Position.X - double target.Position.X
                    let visible = globalIndex observer = source || (origin.Location.LocationId = target.Location.LocationId && dx * dx <= 8192. * 8192.)
                    if visible then
                        if seen[observer, source] <> target.SampledAtUs || seenX[observer, source] <> target.Position.X then valid <- false
                    elif seen[observer, source] <> 0UL then valid <- false
        finalConverged <- valid
        valid

    member _.Report(loadMs) =
        {| scenario = scenario; clients = count; sourceHz = rate; replicationMs = replicationMs; visibilityDistance = 8192
           warmPositions = warmPositions.Length > 0
           sentSamples = sent; receivedMovements = received; visibilityClears = cleared
           sentPayloadBytes = sentBytes; receivedPayloadBytes = receivedBytes
           sentPayloadBytesPerSecond = float sentBytes * 1000. / max 1. loadMs
           receivedPayloadBytesPerSecond = float receivedBytes * 1000. / max 1. loadMs
           actualSamplesPerClientSecond = float sent * 1000. / (max 1. loadMs * float count)
           generatorMissedIntervals = missed; pendingThrottledSamples = 0L; maxPendingPerClient = maxPending
           initializedPairs = initializedCount; expectedInitializedPairs = count * total
           finalStateConverged = finalConverged; pending = pending |> Array.sumBy _.Count
           deliveryAgeMs = ages.Summary(); receiveGapMs = receiveGaps.Summary(); controlAckMs = acknowledgements.Summary()
           crossProcessMovements = crossProcess; serviceIterationMs = iterations.Summary()
           percentileMethod = "fixed logarithmic histogram, upper bounds within 1% + 0.01 ms" |}

// Deterministic checks of the load generator's oracle, without sockets or load.
let verifyOracle () =
    let controls = ResizeArray<ClientPacket>()
    let samples = ResizeArray<ClientMovementPacket>()
    let mutable time = 1000.
    let fail message = invalidOp message
    let check condition message = if not condition then fail message
    let probe = Probe("dense", 20., 50, [| 1UL |], 0, 1, 1, (fun () -> time),
                      (fun packetIndex packet -> check (packetIndex = 0) "Unexpected sender"; controls.Add packet; true),
                      (fun packetIndex packet -> check (packetIndex = 0) "Unexpected sender"; samples.Add packet; true), fail)
    probe.Start(0.)
    probe.SendDue()
    check (controls.Count = 1 && samples.Count = 1) "Initial context and sample must use separate envelopes"
    let initial = controls[0].UpdatePlayer.SetLocation.Location
    let acknowledge = ServerPacket(RequestId = controls[0].RequestId, PlayerUpdateAccepted = PlayerUpdateAccepted())
    probe.Receive(0, acknowledge, 0)
    check (probe.Pending = 0) "Movement must not create pending requests"

    let baseline revision location =
        probe.Receive(0, ServerPacket(PlayerVisibilityChanged = PlayerVisibilityChanged(
            PlayerId = 1UL, ViewRevision = revision, Sequence = 0UL, Location = location)), 0)
    let movement revision sequence pose =
        let values = PlayersMoved()
        values.Players.Add(PlayerMoved(PlayerId = 1UL, ViewRevision = revision, Sequence = sequence, Pose = pose))
        probe.ReceiveMovement(0, ServerMovementPacket(Movements = values), 0)
    let wrong = MovementPose(Position = Position(X = -999.f), Rotation = Rotation(), SampledAtUs = 1UL)

    movement 1UL 1UL samples[0].Sample.Pose
    check (not (probe.Converged())) "Realtime must not establish visibility before control baseline"
    baseline 1UL initial
    check (probe.Converged()) "Sequence-zero control baseline must establish final position"
    movement 2UL 1UL wrong
    check (probe.Converged()) "Future view must wait for its reliable baseline"
    movement 1UL 0UL wrong
    check (probe.Converged()) "Older or repeated sequence must not replace the baseline"
    movement 1UL 1UL samples[0].Sample.Pose
    movement 1UL 1UL wrong
    check (probe.Converged()) "Duplicate sample must not overwrite accepted position"
    baseline 2UL null
    movement 1UL 100UL samples[0].Sample.Pose
    check (not (probe.Converged())) "Old-view movement must not resurrect cleared visibility"
    baseline 3UL initial
    check (probe.Converged()) "New baseline must restore visibility"

    probe.Stop()
    time <- 2000.
    probe.FinalSamples(ignore)
    let lost = samples[samples.Count - 1].Sample
    time <- 3000.
    probe.RepeatFinalSamples()
    let repeated = samples[samples.Count - 1].Sample
    check (repeated.Sequence > lost.Sequence && repeated.Pose = lost.Pose) "Drain must repeat the frozen final pose with a fresh sequence"
    check (probe.Pending = 0) "Final repeats must not wait for acknowledgements"
    movement 3UL repeated.Sequence repeated.Pose
    check (probe.Converged()) "A later repeat must recover the lost final sample"
    let warmControls = ResizeArray<ClientPacket>()
    let warmSamples = ResizeArray<ClientMovementPacket>()
    let warm = Probe("spaces", 20., 50, [|1UL|], 0, 1, 1, (fun () -> time),
                     (fun _ packet -> warmControls.Add packet; true),
                     (fun _ packet -> warmSamples.Add packet; true), fail)
    check (not warm.PositionsPrepared) "Cold mode must not report warmup completion"
    warm.PreparePositions(ignore)
    check (warmControls.Count = 1 && not warm.PositionsPrepared) "Warmup waits for reliable ACK and visibility"
    let context = warmControls[0].UpdatePlayer.SetLocation
    warm.Receive(0, ServerPacket(RequestId = warmControls[0].RequestId, PlayerUpdateAccepted = PlayerUpdateAccepted()), 0)
    let remoteBaseline = context.Location.Clone()
    remoteBaseline.SampledAtUs <- remoteBaseline.SampledAtUs + 999UL
    warm.Receive(0, ServerPacket(PlayerVisibilityChanged = PlayerVisibilityChanged(
        PlayerId = 1UL, ViewRevision = 1UL, Sequence = 0UL, Location = remoteBaseline)), 0)
    check warm.PositionsPrepared "Warmup compares expected coordinates/context, not another worker's timestamp"
    time <- time + 1000.
    warm.Start(time)
    warm.SendDue()
    check (warmControls.Count = 1) "Steady-state first frame must reuse the warmed location context"
    check (warmSamples.Count = 2) "Measured movement still emits a fresh sample after warmup"
    printfn "Movement oracle checks passed."
