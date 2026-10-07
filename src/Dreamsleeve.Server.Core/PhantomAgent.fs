namespace Dreamsleeve.Server.Core

open System
open System.Collections.Generic
open System.Diagnostics.Metrics
open System.Threading.Tasks
open Dreamsleeve.Server.Domain

/// Serialized by ServerRuntime. IO tasks belong to the detached storage worker;
/// this owner only admits work, polls bounded completions, and retains latest poses.
[<RequireQualifiedAccess>]
module PhantomAgent =
    let private meter = new Meter("Dreamsleeve.Phantoms")
    let private rejected = meter.CreateCounter<int64>("phantom.rejected")
    let private uploaded = meter.CreateCounter<int64>("phantom.upload.bytes", "bytes")
    let private downloaded = meter.CreateCounter<int64>("phantom.download.bytes", "bytes")
    let private fanout = meter.CreateCounter<int64>("phantom.pose.fanout")
    let private lifecycle = meter.CreateCounter<int64>("phantom.lifecycle")
    let private traceFor receiver kind (player: PlayerId) context generation view reason =
        if lifecycle.Enabled then
            let mutable tags = Diagnostics.TagList()
            tags.Add("receiver", receiver); tags.Add("event", kind); tags.Add("player", uint64 player); tags.Add("context", context)
            tags.Add("generation", generation); tags.Add("view", view); tags.Add("reason", reason)
            lifecycle.Add(1L, &tags)
    let private trace kind player context generation view reason = traceFor 0UL kind player context generation view reason
    type private Credit = { mutable At: int64; mutable Available: double }
    type private View = { Authority: uint64; Distance: double; Facing: double option }
    type private Selected = { Revision: uint64; Asset: PhantomManifest; Authority: uint64; mutable SentSequence: uint64; mutable SentGeneration: AppearanceGeneration option; mutable Displayed: AppearanceGeneration option; mutable DisplayProgressAt: int64; mutable DisplayProgressBytes: int }
    type private Member = {
        Player: PlayerId; mutable Character: uint64; mutable Context: uint64; mutable Located: bool; mutable Active: bool
        mutable Demand: struct (uint64 * bool) option; mutable Preferences: PhantomPreferences; Views: Dictionary<PlayerId, View>; Selected: Dictionary<PlayerId, Selected>
        mutable ClearedAuthority: uint64; mutable Revision: uint64; mutable HighManifest: PhantomManifest option
        mutable Ready: PhantomManifest option; mutable Latest: struct (PhantomPose * int64) option
        mutable Previous: AppearanceGeneration option; mutable Settled: AppearanceGeneration option; mutable CommittedAt: int64
        mutable DispatchedPose: struct (AppearanceGeneration * uint64) option
        mutable PreviousSequence: uint64; mutable LastPose: struct (AppearanceGeneration * uint64) option; mutable NextPublish: int64; mutable PoseCursor: int
        ModelCredit: Credit; PoseCredit: Credit; PoseSamples: Credit; ReplicationCredit: Credit; OutgoingPoseCredit: Credit; Commands: Credit; Outbox: Queue<TransportPacket>
    }
    type private UploadChunk = { Offset: int; Bytes: byte array; mutable Pending: Task<Result<bool, string>> option }
    type private Phase =
        | StartingUpload of Task<Result<bool, string>>
        | Uploading of Queue<UploadChunk>
        | StartingDownload of Task<Result<unit, string>>
        | Downloading of Queue<struct (int * Task<Result<byte array, string>>)>
    type private Transfer = {
        Id: PhantomTransferId; Request: PhantomRequestId; Owner: Guid; Source: PlayerId; Manifest: PhantomManifest
        Character: uint64; Context: uint64; View: uint64; Upload: bool
        Flow: ModelFlow.State; mutable Offset: int; mutable Sent: int; mutable Acknowledged: int; mutable Touched: int64; mutable Phase: Phase
    }
    type Snapshot = { Members: int; Sources: int; Subscriptions: int; Transfers: int; LatestPoses: int; PendingIo: int }
    type State = private {
        Options: PhantomOptions; Storage: PhantomStoragePort; Send: Guid * TransportPacket -> Result<unit, string>
        Members: Dictionary<Guid, Member>; Players: Dictionary<PlayerId, Guid>; Transfers: Dictionary<PhantomTransferId, Transfer>
        Audiences: Dictionary<PlayerId, HashSet<Guid>>
        Cleanup: ResizeArray<Task<unit>>; ModelCredit: Credit; OutgoingPoseCredit: Credit; mutable NextTransfer: uint64; mutable LastTick: int64; mutable LastDispatch: int64; FanoutCredit: Credit; mutable Cursor: int; mutable TransferCursor: int
    }

    let private credit at rate = { At = at; Available = double rate }
    let private refillBounded at capacity rate (value: Credit) =
        value.Available <- min capacity (value.Available + double (max 0L (at - value.At)) * rate / 1000.0)
        value.At <- at
    let private refill at rate value = refillBounded at (double rate) (double rate) value
    // The ACK window bounds retained data; send credit bounds new bursts.
    // Two chunks leave room for transport headers and concurrent realtime data.
    let private refillModel at rate chunkBytes value =
        refillBounded at (double (2 * chunkBytes)) (double rate) value
    let private take at rate amount value =
        refill at rate value
        if value.Available < double amount then false
        else value.Available <- value.Available - double amount; true
    let private takeCommand at rate value =
        refill at rate value
        value.Available <- min 8.0 value.Available
        if value.Available < 1.0 then false
        else value.Available <- value.Available - 1.0; true
    let private deny () = rejected.Add 1L
    let private nextRevision (memberState: Member) = memberState.Revision <- memberState.Revision + 1UL; memberState.Revision
    let private emit state id response =
        match state.Members.TryGetValue id with
        | true, memberState when memberState.Active ->
            if memberState.Outbox.Count < 2 * state.Options.Maximum + 4 * state.Options.TransfersPerPlayer + 16 then
                memberState.Outbox.Enqueue(PhantomCodec.encode response)
            else
                // A bounded lifecycle backlog must not accumulate binary assets.
                memberState.Active <- false
                memberState.Latest <- None
                deny()
        | _ -> ()
    let private source state player =
        let mutable id = Guid.Empty
        let mutable memberState = Unchecked.defaultof<Member>
        if state.Players.TryGetValue(player, &id) && state.Members.TryGetValue(id, &memberState) then ValueSome memberState
        else ValueNone
    let private cancel state id accepted reason =
        match state.Transfers.TryGetValue id with
        | true, transfer ->
            trace (if accepted then "transfer_complete" else "transfer_cancel") transfer.Source transfer.Context transfer.Manifest.Generation.Value transfer.View reason
            state.Transfers.Remove id |> ignore
            state.Cleanup.Add(state.Storage.Cancel id)
            let terminal =
                match reason with
                | "source changed" | "context changed" | "view removed" | "disconnected" | "cancelled" | "chunk admission"
                | "stale transfer" | "superseded" | "hash mismatch" | "cache integrity" | "chunk offset/size" | "read bounds" -> true
                | _ -> false
            let completion = { Target = { Player = transfer.Source; Generation = transfer.Manifest.Generation }
                               Upload = transfer.Upload; RetryAfterMs = if accepted || terminal then 0 else (if transfer.Upload then max 1 state.Options.PublishCooldownMs else 1000) }
            let reason = if Text.Encoding.UTF8.GetByteCount reason <= 256 then reason else "storage failure"
            let responseId = match transfer.Phase with StartingUpload _ | StartingDownload _ -> PhantomTransferId 0UL | _ -> id
            emit state transfer.Owner (PhantomResponse.Complete(responseId, accepted, reason, transfer.Request, Some completion))
        | _ -> ()
    let private clearSource state (memberState: Member) =
        trace "source_clear" memberState.Player memberState.Context (memberState.Ready |> Option.map (fun x -> x.Generation.Value) |> Option.defaultValue 0UL) 0UL ""
        memberState.Ready <- None
        memberState.Previous <- None; memberState.PreviousSequence <- 0UL
        memberState.Settled <- None
        memberState.Latest <- None
        let affected = state.Transfers.Values |> Seq.filter (fun item -> item.Source = memberState.Player) |> Seq.map _.Id |> Seq.toArray
        for id in affected do cancel state id false "source changed"
    let private valid state (transfer: Transfer) =
        match state.Members.TryGetValue transfer.Owner, source state transfer.Source with
        | (true, owner), ValueSome current when owner.Active && current.Active && current.Located
                                         && current.Character = transfer.Character && current.Context = transfer.Context ->
            if transfer.Upload then current.Preferences.Publish && current.HighManifest = Some transfer.Manifest
            else
                match owner.Selected.TryGetValue transfer.Source with
                | true, selected -> selected.Revision = transfer.View && selected.Asset = transfer.Manifest
                | _ -> false
        | _ -> false
    let private refreshEnabled state =
        let subscribers = Dictionary<PlayerId, int>()
        for observer in state.Members.Values do
            for player in observer.Selected.Keys do
                let count = match subscribers.TryGetValue player with true, count -> count | _ -> 0
                subscribers[player] <- count + 1
        for KeyValue(id, observer) in state.Members do
            let candidates = seq {
                if observer.Active && observer.Preferences.Receive && observer.Preferences.Maximum > 0 then
                    for KeyValue(player, view) in observer.Views do
                        match source state player with
                        | ValueSome current when current.Active && current.Located && current.Preferences.Publish && current.Ready.IsSome ->
                            let count = match subscribers.TryGetValue player with true, count -> count | _ -> 0
                            let retained = observer.Selected.ContainsKey player
                            if (retained || count < state.Options.MaxSubscribers)
                               && (not state.Options.CameraCulling || PhantomPolicy.inView retained view.Distance view.Facing) then
                                yield player, view.Distance
                        | _ -> ()
            }
            let selection = PhantomPolicy.select observer.Preferences (observer.Selected.Keys |> Set.ofSeq) candidates
            for player in observer.Selected.Keys |> Seq.toArray do
                let selected = observer.Selected[player]
                let retained =
                    selection.Contains player && observer.Views.ContainsKey player
                    && observer.Views[player].Authority = selected.Authority
                if not retained then
                    traceFor (uint64 observer.Player) "view_removed" player observer.Context selected.Asset.Generation.Value selected.Revision
                        (if not (observer.Views.ContainsKey player) then "aoi-departure" elif observer.Views[player].Authority <> selected.Authority then "authority-changed" else "policy-selection")
                    observer.Selected.Remove player |> ignore
                    match state.Audiences.TryGetValue player with
                    | true, audience ->
                        audience.Remove id |> ignore
                        if audience.Count = 0 then state.Audiences.Remove player |> ignore
                    | _ -> ()
                    subscribers[player] <- subscribers[player] - 1
                    emit state id (PhantomResponse.Remove(player, nextRevision observer))
                    for transfer in state.Transfers.Values |> Seq.filter (fun item -> not item.Upload && item.Owner = id && item.Source = player) |> Seq.toArray do
                        cancel state transfer.Id false "view removed"
                else
                    match source state player with
                    | ValueSome current when current.Ready.IsSome && current.Ready.Value <> selected.Asset ->
                        // An appearance replacement is not an AOI departure. Keep
                        // the receiver's previous scene until this offer is ready.
                        for transfer in state.Transfers.Values |> Seq.filter (fun item -> not item.Upload && item.Owner = id && item.Source = player) |> Seq.toArray do
                            cancel state transfer.Id false "superseded"
                        let replacement = { selected with Revision = nextRevision observer; Asset = current.Ready.Value; SentSequence = 0UL; SentGeneration = None; DisplayProgressAt = max current.CommittedAt state.LastTick; DisplayProgressBytes = 0 }
                        observer.Selected[player] <- replacement
                        traceFor (uint64 observer.Player) "offer" player current.Context replacement.Asset.Generation.Value replacement.Revision "replacement"
                        emit state id (PhantomResponse.Offer(player, replacement.Revision, replacement.Asset))
                    | _ -> ()
            for player in selection do
                if not (observer.Selected.ContainsKey player) then
                    match source state player with
                    | ValueSome current when current.Ready.IsSome && observer.Views.ContainsKey player ->
                        let selected = { Revision = nextRevision observer; Asset = current.Ready.Value; Authority = observer.Views[player].Authority; SentSequence = 0UL; SentGeneration = None; Displayed = None; DisplayProgressAt = max current.CommittedAt state.LastTick; DisplayProgressBytes = 0 }
                        observer.Selected[player] <- selected
                        let audience =
                            match state.Audiences.TryGetValue player with
                            | true, audience -> audience
                            | _ -> let audience = HashSet<Guid>() in state.Audiences[player] <- audience; audience
                        audience.Add id |> ignore
                        let count = match subscribers.TryGetValue player with true, count -> count | _ -> 0
                        subscribers[player] <- count + 1
                        traceFor (uint64 observer.Player) "offer" player current.Context selected.Asset.Generation.Value selected.Revision "subscription"
                        emit state id (PhantomResponse.Offer(player, selected.Revision, selected.Asset))
                    | _ -> ()

    let private refresh state =
        if state.Options.Enabled then refreshEnabled state
        for KeyValue(id, memberState) in state.Members do
            let required = state.Options.Enabled && memberState.Active && memberState.Located && memberState.Preferences.Publish
                           && state.Audiences.ContainsKey memberState.Player
            let demand = struct (memberState.Context, required)
            if state.Options.Enabled && memberState.Active && memberState.Context <> 0UL && memberState.Demand <> Some demand then
                memberState.Demand <- Some demand
                if not required then memberState.Latest <- None
                emit state id (PhantomResponse.PoseDemand(memberState.Context, required))

    let create options storage send = {
        Options = options; Storage = storage; Send = send; Members = Dictionary(); Players = Dictionary(); Transfers = Dictionary()
        Cleanup = ResizeArray(); Audiences = Dictionary(); ModelCredit = credit 0L options.ModelBytesPerSecond; NextTransfer = 0UL; LastTick = 0L; LastDispatch = 0L; Cursor = 0; TransferCursor = 0
        FanoutCredit = credit 0L options.MaxPoseFanoutPerTick
        OutgoingPoseCredit = credit 0L options.TotalPoseBytesPerSecond
    }
    let observationMode state =
        if state.Options.Enabled then PhantomObservationMode.Full else PhantomObservationMode.Membership
    let snapshot state = {
        Members = state.Members.Count; Sources = state.Members.Values |> Seq.filter (fun item -> item.Ready.IsSome) |> Seq.length
        Subscriptions = state.Members.Values |> Seq.sumBy (fun item -> item.Selected.Count); Transfers = state.Transfers.Count
        LatestPoses = state.Members.Values |> Seq.filter (fun item -> item.Latest.IsSome) |> Seq.length
        PendingIo = state.Transfers.Values |> Seq.sumBy (fun item -> match item.Phase with Uploading queue -> queue.Count | Downloading queue -> queue.Count | _ -> 1)
    }
    let detach state id =
        match state.Members.TryGetValue id with
        | true, memberState ->
            clearSource state memberState
            for transfer in state.Transfers.Values |> Seq.filter (fun item -> item.Owner = id) |> Seq.toArray do cancel state transfer.Id false "disconnected"
            for player in memberState.Selected.Keys do
                match state.Audiences.TryGetValue player with
                | true, audience ->
                    audience.Remove id |> ignore
                    if audience.Count = 0 then state.Audiences.Remove player |> ignore
                | _ -> ()
            state.Members.Remove id |> ignore
            state.Players.Remove memberState.Player |> ignore
            for observer in state.Members.Values do
                let mutable view = Unchecked.defaultof<View>
                if observer.Views.TryGetValue(memberState.Player, &view) then
                    observer.ClearedAuthority <- max observer.ClearedAuthority view.Authority
                    observer.Views.Remove memberState.Player |> ignore
            refresh state
        | _ -> ()
    let activate state id =
        match state.Members.TryGetValue id with
        | true, memberState when not memberState.Active ->
            memberState.Active <- true
            emit state id (PhantomResponse.Policy(PhantomOptions.policy state.Options))
            refresh state
        | _ -> ()
    let rec observe state observation =
        match observation with
        | PhantomObservation.Member(id, value) ->
            match state.Members.TryGetValue id with
            | true, memberState when memberState.Player = value.Identity.PlayerId ->
                let characterChanged = memberState.Character <> value.CharacterGeneration
                let contextChanged = memberState.Context <> value.MovementContext || (memberState.Located && value.Location.IsNone)
                if characterChanged || contextChanged then
                    trace "context" memberState.Player value.MovementContext 0UL 0UL (if characterChanged then "character" else "movement")
                if characterChanged then
                    clearSource state memberState
                    memberState.LastPose <- None
                    memberState.DispatchedPose <- None
                elif contextChanged then
                    memberState.Latest <- None
                    memberState.LastPose <- None
                    memberState.DispatchedPose <- None
                    memberState.Previous <- None; memberState.PreviousSequence <- 0UL
                    for transfer in state.Transfers.Values |> Seq.filter (fun item -> item.Source = memberState.Player) |> Seq.toArray do
                        cancel state transfer.Id false "context changed"
                memberState.Character <- value.CharacterGeneration
                memberState.Context <- value.MovementContext
                memberState.Located <- value.Location.IsSome && value.MovementContext <> 0UL
                if characterChanged || contextChanged then
                    for view in memberState.Views.Values do memberState.ClearedAuthority <- max memberState.ClearedAuthority view.Authority
                    memberState.Views.Clear()
                    for observer in state.Members.Values do
                        match observer.Views.TryGetValue memberState.Player with
                        | true, view ->
                            observer.ClearedAuthority <- max observer.ClearedAuthority view.Authority
                            observer.Views.Remove memberState.Player |> ignore
                        | _ -> ()
                    refresh state
            | false, _ when state.Members.Count < ServerConfig.MaxPeerLimit && not (state.Players.ContainsKey value.Identity.PlayerId) ->
                let options = state.Options
                state.Members[id] <- {
                    Player = value.Identity.PlayerId; Character = value.CharacterGeneration; Context = value.MovementContext
                    Located = value.Location.IsSome && value.MovementContext <> 0UL; Active = false
                    Preferences = { Publish = options.Enabled; Receive = options.Enabled; Maximum = options.Maximum; Distance = options.Distance }
                    Demand = None; Views = Dictionary(); Selected = Dictionary(); ClearedAuthority = 0UL; Revision = 0UL; HighManifest = None
                    Ready = None; Latest = None; Previous = None; Settled = None; CommittedAt = 0L; PreviousSequence = 0UL; LastPose = None; DispatchedPose = None; NextPublish = 0L; PoseCursor = 0
                    ModelCredit = credit 0L options.PlayerModelBytesPerSecond; PoseCredit = credit 0L options.PoseBytesPerSecond
                    OutgoingPoseCredit = credit 0L options.PoseBytesPerSecond; PoseSamples = credit 0L 2; ReplicationCredit = credit 0L 2
                    Commands = credit 0L options.CommandsPerSecond; Outbox = Queue()
                }
                state.Players[value.Identity.PlayerId] <- id
            | _ -> ()
        | PhantomObservation.View(id, sourceConnection, player, revision, distance, facing) ->
            let mutable observer = Unchecked.defaultof<Member>
            let mutable currentSource = Guid.Empty
            // Presence captures the source session epoch when it projects a view.
            // A queued fact cannot bind an absent or reconnected PlayerId.
            if state.Members.TryGetValue(id, &observer) && observer.Player <> player
               && state.Players.TryGetValue(player, &currentSource) && currentSource = sourceConnection
               && Double.IsFinite distance && distance >= 0.0 then
                let mutable view = Unchecked.defaultof<View>
                if observer.Views.TryGetValue(player, &view) then
                    if revision >= view.Authority && (revision <> view.Authority || distance <> view.Distance || facing <> view.Facing) then
                        observer.Views[player] <- { Authority = revision; Distance = distance; Facing = facing }
                elif revision > observer.ClearedAuthority then
                    observer.Views[player] <- { Authority = revision; Distance = distance; Facing = facing }
        | PhantomObservation.Hidden(id, player, revision) ->
            match state.Members.TryGetValue id with
            | true, observer ->
                observer.ClearedAuthority <- max observer.ClearedAuthority revision
                match observer.Views.TryGetValue player with
                | true, view when view.Authority < revision -> observer.Views.Remove player |> ignore
                | _ -> ()
            | _ -> ()
        | PhantomObservation.Departed id -> detach state id
        | PhantomObservation.Batch observations -> for observation in observations do observe state observation

    let private transferAvailable state owner =
        state.Cleanup.RemoveAll(Predicate(fun pending -> pending.IsCompleted)) |> ignore
        let count = state.Transfers.Values |> Seq.filter (fun item -> item.Owner = owner) |> Seq.length
        state.Transfers.Count + state.Cleanup.Count < state.Options.MaxTransfers && count < state.Options.TransfersPerPlayer

    let private refuse state owner player generation upload request retry reason =
        let completion = { Target = { Player = player; Generation = generation }; Upload = upload; RetryAfterMs = retry }
        emit state owner (PhantomResponse.Complete(PhantomTransferId 0UL, false, reason, request, Some completion))
        deny()

    let private startTransfer state at owner player (manifest: PhantomManifest) character context revision upload request =
        if not (transferAvailable state owner) then
            refuse state owner player manifest.Generation upload request (if upload then max 1 state.Options.PublishCooldownMs else 1000) "transfer limit"
        else
            state.NextTransfer <- state.NextTransfer + 1UL
            let id = PhantomTransferId state.NextTransfer
            let phase = if upload then StartingUpload(state.Storage.StartUpload(id, manifest)) else StartingDownload(state.Storage.StartDownload(id, manifest))
            state.Transfers[id] <- { Id = id; Request = request; Owner = owner; Source = player; Manifest = manifest; Character = character; Context = context
                                     View = revision; Upload = upload; Flow = ModelFlow.create state.Options.ChunkBytes state.Options.WindowChunks; Offset = 0; Sent = 0; Acknowledged = 0; Touched = at; Phase = phase }

    let handle state at id request =
        match state.Members.TryGetValue id with
        | true, memberState when memberState.Active && state.Options.Enabled ->
            match request with
            | PhantomRequest.Preferences preferences ->
                memberState.Preferences <- PhantomPolicy.effective state.Options.Maximum state.Options.Distance preferences
                if not preferences.Publish then clearSource state memberState
                refresh state
            | PhantomRequest.Withdraw -> clearSource state memberState; refresh state
            | PhantomRequest.Publish(manifest, context, request) ->
                trace "publish_request" memberState.Player context manifest.Generation.Value 0UL ""
                let sources = state.Members.Values |> Seq.filter (fun item -> item.Ready.IsSome) |> Seq.length
                let pendingSources = state.Transfers.Values |> Seq.filter (fun item ->
                    item.Upload && (match source state item.Source with ValueSome current -> current.Ready.IsNone | ValueNone -> false)) |> Seq.length
                let existingSource = memberState.Ready.IsSome || (state.Transfers.Values |> Seq.exists (fun item -> item.Upload && item.Owner = id))
                let stale = memberState.HighManifest |> Option.exists (fun highest ->
                    manifest.Generation.Value < highest.Generation.Value || (manifest.Generation = highest.Generation && manifest <> highest))
                if not memberState.Preferences.Publish || not memberState.Located || context <> memberState.Context
                   || stale then
                    refuse state id memberState.Player manifest.Generation true request 0 "publish admission"
                elif memberState.Ready |> Option.exists (fun ready -> memberState.Settled <> Some ready.Generation && manifest <> ready) then
                    refuse state id memberState.Player manifest.Generation true request 1000 "replacement pending"
                elif at < memberState.NextPublish then
                    refuse state id memberState.Player manifest.Generation true request (int (memberState.NextPublish - at)) "publish cooldown"
                elif not existingSource && sources + pendingSources >= state.Options.MaxSources then
                    refuse state id memberState.Player manifest.Generation true request (max 1 state.Options.PublishCooldownMs) "source limit"
                elif not (transferAvailable state id) then
                    refuse state id memberState.Player manifest.Generation true request (max 1 state.Options.PublishCooldownMs) "transfer limit"
                else
                    // Pending publication owns no visible scene. Supersede only
                    // uploads; current poses and downloads remain usable meanwhile.
                    for transfer in state.Transfers.Values |> Seq.filter (fun item -> item.Upload && item.Owner = id) |> Seq.toArray do
                        cancel state transfer.Id false "superseded"
                    memberState.Previous <- memberState.Ready |> Option.map _.Generation
                    match memberState.LastPose with
                    | Some struct (generation, sequence) when memberState.Previous = Some generation -> memberState.PreviousSequence <- sequence
                    | _ -> ()
                    memberState.HighManifest <- Some manifest
                    memberState.NextPublish <- at + int64 state.Options.PublishCooldownMs
                    startTransfer state at id memberState.Player manifest memberState.Character context 0UL true request
                    refresh state
            | PhantomRequest.Displayed(player, revision, generation) ->
                match memberState.Selected.TryGetValue player with
                | true, selected when selected.Revision = revision && selected.Asset.Generation = generation ->
                    traceFor (uint64 memberState.Player) "displayed" player memberState.Context generation.Value revision ""
                    selected.Displayed <- Some generation
                | _ -> deny()
            | PhantomRequest.Download(player, generation, request) ->
                match memberState.Selected.TryGetValue player, source state player with
                | (true, selected), ValueSome current when selected.Asset.Generation = generation ->
                    let previous = state.Transfers.Values |> Seq.filter (fun item -> item.Owner = id && not item.Upload && item.Source = player) |> Seq.toArray
                    if previous |> Array.forall (fun item -> item.Request <> request) then
                        for transfer in previous do cancel state transfer.Id false "superseded"
                        startTransfer state at id player selected.Asset current.Character current.Context selected.Revision false request
                | _ -> refuse state id player generation false request 1000 "view unavailable"
            | PhantomRequest.Cancel transferId ->
                match state.Transfers.TryGetValue transferId with
                | true, transfer when transfer.Owner = id -> cancel state transferId false "cancelled"
                | _ -> deny()
            | PhantomRequest.Progress(transferId, offset) ->
                match state.Transfers.TryGetValue transferId with
                | true, transfer when transfer.Owner = id && not transfer.Upload && valid state transfer
                                      && offset >= transfer.Acknowledged && offset <= transfer.Sent
                                      && (offset % state.Options.ChunkBytes = 0 || offset = transfer.Manifest.CompressedBytes) ->
                    if offset > transfer.Acknowledged then
                        ModelFlow.acknowledge offset at transfer.Flow
                        transfer.Acknowledged <- offset
                        transfer.Touched <- at
                        // Only actual delivery progress extends the display grace.
                        // Retrying the same prefix in a new transfer is not new progress.
                        match memberState.Selected.TryGetValue transfer.Source with
                        | true, selected when selected.Revision = transfer.View && selected.Asset = transfer.Manifest
                                              && offset > selected.DisplayProgressBytes ->
                            selected.DisplayProgressBytes <- offset
                            selected.DisplayProgressAt <- at
                        | _ -> ()
                | _ -> deny()
            | PhantomRequest.Chunk(transferId, offset, bytes) ->
                match state.Transfers.TryGetValue transferId with
                | true, transfer when transfer.Owner = id && transfer.Upload && valid state transfer ->
                    match transfer.Phase with
                    | Uploading queue when queue.Count < state.Options.WindowChunks && offset = transfer.Offset
                                           && bytes.Length > 0 && bytes.Length <= state.Options.ChunkBytes
                                           && bytes.Length <= transfer.Manifest.CompressedBytes - offset
                                           && int64 offset + int64 bytes.Length - int64 transfer.Acknowledged <= int64 state.Options.WindowChunks * int64 state.Options.ChunkBytes
                                           ->
                        queue.Enqueue { Offset = offset; Bytes = bytes; Pending = None }
                        transfer.Offset <- transfer.Offset + bytes.Length
                        transfer.Touched <- at
                    | _ -> cancel state transferId false "chunk admission"; deny()
                | _ -> deny()
        | _ -> deny()

    /// Large poses are admitted before protobuf allocation. Model data uses the
    /// admitted transfer window; control requests have a separate burst budget.
    let receive state at id lane (bytes: byte array) =
        match state.Members.TryGetValue id with
        | true, memberState when not memberState.Active && memberState.Outbox.Count = 0 -> activate state id
        | _ -> ()
        match state.Members.TryGetValue id with
        | true, memberState when memberState.Active && state.Options.Enabled ->
            if lane = DeliveryLane.Poses then
                memberState.PoseSamples.Available <- min 2.0 (memberState.PoseSamples.Available + double (at - memberState.PoseSamples.At) / double state.Options.PoseIntervalMs)
                memberState.PoseSamples.At <- at
                if memberState.PoseSamples.Available >= 1.0 && take at state.Options.PoseBytesPerSecond bytes.Length memberState.PoseCredit then
                    memberState.PoseSamples.Available <- memberState.PoseSamples.Available - 1.0
                    match PhantomCodec.decodePose state.Options bytes with
                    | Ok pose when memberState.Located && memberState.Preferences.Publish && pose.Context = memberState.Context ->
                        let pending = if memberState.Previous.IsSome then memberState.HighManifest else None
                        let known = [memberState.Ready; pending] |> List.exists (Option.exists (fun asset -> asset.Generation = pose.Generation))
                        // A failed pending upload may fall back to its still-ready model.
                        // Keep both sequence floors: accepting that fallback must not reopen
                        // the pending generation to delayed/reordered packets.
                        let fallback = memberState.Previous = Some pose.Generation &&
                                       (memberState.Ready |> Option.exists (fun ready -> ready.Generation = pose.Generation))
                        let advancing = match memberState.LastPose with
                                        | Some struct (generation, sequence) when generation = pose.Generation -> pose.Sequence.Value > sequence
                                        | Some struct (generation, _) -> pose.Generation.Value > generation.Value || (fallback && pose.Sequence.Value > memberState.PreviousSequence)
                                        | None -> true
                        let validPrevious = pose.Previous |> Option.forall (fun previous ->
                            memberState.Previous = Some previous.Generation || memberState.Settled = Some pose.Generation)
                        if known && advancing && validPrevious then
                            let accepted = if memberState.Previous.IsNone then PhantomPose.withoutPrevious pose else pose
                            if fallback then memberState.PreviousSequence <- pose.Sequence.Value
                            match memberState.LastPose with
                            | Some struct (generation, _) when generation.Value > pose.Generation.Value -> ()
                            | _ -> memberState.LastPose <- Some(struct (pose.Generation, pose.Sequence.Value))
                            pose.Previous |> Option.iter (fun previous -> memberState.PreviousSequence <- max memberState.PreviousSequence previous.Sequence.Value)
                            memberState.Latest <- Some(struct (accepted, at))
                        else deny()
                    | _ -> deny()
                else deny()
            elif lane = DeliveryLane.Models then
                match PhantomCodec.decodeAsset state.Options bytes with
                // Transfer windows and byte credits govern chunks/ACKs. Control
                // request bursts must not drop an admitted reliable data window.
                | Ok (PhantomRequest.Chunk _ as request) | Ok (PhantomRequest.Progress _ as request) -> handle state at id request
                | Ok request when memberState.Outbox.Count < 8 && takeCommand at state.Options.CommandsPerSecond memberState.Commands -> handle state at id request
                | Ok _ -> deny()
                | Error _ -> deny()
            else deny()
        | _ -> deny()

    let private flushOutbox state id (memberState: Member) =
        let mutable sending = true
        let mutable count = 0
        while sending && memberState.Outbox.Count > 0 && count < 8 do
            match state.Send(id, memberState.Outbox.Peek()) with
            | Ok () -> memberState.Outbox.Dequeue() |> ignore; count <- count + 1
            | Error _ -> sending <- false

    let private settle state at (transfer: Transfer) =
        let mutable serviced = false
        if not (valid state transfer) then cancel state transfer.Id false "stale transfer"
        elif at - transfer.Touched > int64 state.Options.TransferTimeoutMs then cancel state transfer.Id false "transfer timeout"
        else
            let ready () =
                let owner = state.Members[transfer.Owner]
                if owner.Ready <> Some transfer.Manifest then
                    // A pose for the pending generation can already be admitted.
                    // Commit must not reopen its sequence floor or discard it.
                    owner.Latest <- owner.Latest |> Option.filter (fun struct (pose, _) -> pose.Generation = transfer.Manifest.Generation)
                trace "committed" owner.Player owner.Context transfer.Manifest.Generation.Value 0UL ""
                owner.Ready <- Some transfer.Manifest
                owner.CommittedAt <- at
                cancel state transfer.Id true ""
            match transfer.Phase with
            | StartingUpload pending when pending.IsCompleted ->
                match pending.Result with
                | Error reason -> cancel state transfer.Id false reason
                | Ok cached ->
                    transfer.Phase <- Uploading(Queue())
                    emit state transfer.Owner (PhantomResponse.Transfer(transfer.Id, transfer.Manifest, transfer.Source, true, transfer.Request))
                    if cached then ready()
            | Uploading queue ->
                let owner = state.Members[transfer.Owner]
                // A full network window is retained, but IO and ACKs are paced by
                // shared traffic credits. Temporary bandwidth contention cannot
                // discard an otherwise valid reliable upload.
                let mutable admitting = true
                for chunk in queue do
                    if admitting && chunk.Pending.IsNone then
                        refillModel at state.Options.PlayerModelBytesPerSecond state.Options.ChunkBytes owner.ModelCredit
                        refillModel at state.Options.ModelBytesPerSecond state.Options.ChunkBytes state.ModelCredit
                        if owner.ModelCredit.Available >= double chunk.Bytes.Length && state.ModelCredit.Available >= double chunk.Bytes.Length then
                            owner.ModelCredit.Available <- owner.ModelCredit.Available - double chunk.Bytes.Length
                            state.ModelCredit.Available <- state.ModelCredit.Available - double chunk.Bytes.Length
                            serviced <- true
                            chunk.Pending <- Some(state.Storage.WriteChunk(transfer.Id, chunk.Offset, chunk.Bytes))
                            uploaded.Add(int64 chunk.Bytes.Length)
                        else admitting <- false
                let mutable checking = true
                let mutable progress = -1
                while checking && queue.Count > 0 && (queue.Peek().Pending |> Option.exists _.IsCompleted) && state.Transfers.ContainsKey transfer.Id do
                    let chunk = queue.Dequeue()
                    let offset = chunk.Offset + chunk.Bytes.Length
                    match chunk.Pending.Value.Result with
                    | Error reason -> cancel state transfer.Id false reason; checking <- false
                    | Ok complete ->
                        transfer.Acknowledged <- offset
                        transfer.Touched <- at
                        progress <- offset
                        if complete then
                            emit state transfer.Owner (PhantomResponse.Progress(transfer.Id, offset))
                            progress <- -1
                            ready()
                            checking <- false
                if progress >= 0 then emit state transfer.Owner (PhantomResponse.Progress(transfer.Id, progress))
            | StartingDownload pending when pending.IsCompleted ->
                match pending.Result with
                | Error reason -> cancel state transfer.Id false reason
                | Ok () ->
                    emit state transfer.Owner (PhantomResponse.Transfer(transfer.Id, transfer.Manifest, transfer.Source, false, transfer.Request))
                    transfer.Phase <- Downloading(Queue())
            | Downloading queue ->
                let owner = state.Members[transfer.Owner]
                if owner.Outbox.Count = 0 then
                    let mutable sending = true
                    while sending && queue.Count > 0 && (let struct (_, pending) = queue.Peek() in pending.IsCompleted) do
                        let struct (offset, pending) = queue.Peek()
                        match pending.Result with
                        | Error reason -> cancel state transfer.Id false reason; sending <- false
                        | Ok bytes ->
                            refillModel at state.Options.PlayerModelBytesPerSecond state.Options.ChunkBytes owner.ModelCredit
                            refillModel at state.Options.ModelBytesPerSecond state.Options.ChunkBytes state.ModelCredit
                            if owner.ModelCredit.Available < double bytes.Length || state.ModelCredit.Available < double bytes.Length
                               || not (ModelFlow.allows (transfer.Sent - transfer.Acknowledged) bytes.Length transfer.Flow) then sending <- false
                            else
                                match state.Send(transfer.Owner, PhantomCodec.encode (PhantomResponse.Chunk(transfer.Id, offset, bytes))) with
                                | Error _ -> sending <- false
                                | Ok () ->
                                    queue.Dequeue() |> ignore
                                    owner.ModelCredit.Available <- owner.ModelCredit.Available - double bytes.Length
                                    state.ModelCredit.Available <- state.ModelCredit.Available - double bytes.Length
                                    serviced <- true
                                    transfer.Sent <- transfer.Sent + bytes.Length
                                    ModelFlow.sent transfer.Sent at transfer.Flow
                                    transfer.Touched <- at
                                    downloaded.Add(int64 bytes.Length)
                    if state.Transfers.ContainsKey transfer.Id then
                        if transfer.Acknowledged = transfer.Manifest.CompressedBytes then cancel state transfer.Id true ""
                        else
                            while queue.Count < state.Options.WindowChunks && transfer.Offset < transfer.Manifest.CompressedBytes
                                  && int64 transfer.Offset - int64 transfer.Acknowledged < int64 state.Options.WindowChunks * int64 state.Options.ChunkBytes do
                                let length = min state.Options.ChunkBytes (transfer.Manifest.CompressedBytes - transfer.Offset)
                                queue.Enqueue(struct (transfer.Offset, state.Storage.ReadChunk(transfer.Id, transfer.Offset, length)))
                                transfer.Offset <- transfer.Offset + length
            | StartingUpload _ | StartingDownload _ -> ()
        serviced

    let tick state at =
        // Cleanup completions are bounded by admitted transfers, and drained even
        // when publishing is disabled. No file work runs inside this turn.
        state.Cleanup.RemoveAll(Predicate(fun pending -> pending.IsCompleted)) |> ignore
        let transfers = state.Transfers.Values |> Seq.toArray
        let start = state.TransferCursor
        for index in 0 .. transfers.Length - 1 do
            let current = (start + index) % transfers.Length
            if settle state at transfers[current] then
                state.TransferCursor <- (current + 1) % transfers.Length
        let period = int64 state.Options.ReplicationIntervalMs
        let replicate = at - state.LastTick >= period
        if replicate then
            // Keep the cadence grid across late runtime ticks; one latest pose,
            // never a burst of duplicate catch-up snapshots.
            state.LastTick <- at - (at - state.LastTick) % period
            refresh state
            for KeyValue(id, owner) in state.Members do
                match owner.Ready with
                | Some ready when owner.Settled <> Some ready.Generation && owner.HighManifest = Some ready ->
                    let waiting =
                        match state.Audiences.TryGetValue owner.Player with
                        | true, audience -> audience |> Seq.exists (fun peer ->
                            match state.Members[peer].Selected.TryGetValue owner.Player with
                            | true, selected -> selected.Displayed <> Some ready.Generation
                                                && at - selected.DisplayProgressAt < int64 state.Options.TransferTimeoutMs
                            | _ -> false)
                        | _ -> false
                    if not waiting then
                        owner.Settled <- Some ready.Generation
                        owner.Previous <- None; owner.PreviousSequence <- 0UL
                        owner.Latest <- owner.Latest |> Option.map (fun struct (pose, received) -> struct (PhantomPose.withoutPrevious pose, received))
                        emit state id (PhantomResponse.Settled(ready.Generation, owner.Context))
                | _ -> ()
        // Reliable control/ACKs follow IO completion, not the pose cadence.
        // At 10 Hz, gating a 64 KiB window here limited uploads to 640 KiB/s.
        for KeyValue(id, memberState) in state.Members do flushOutbox state id memberState
        // Small turns avoid resampling all sources on one 100 ms grid.
        // Source credit preserves the configured rate with a two-snapshot burst;
        // Shared send credit preserves aggregate throughput; each turn still has a
        // bounded inspection budget, including subscriptions already up to date.
        let dispatchPeriod = max 1L (min 25L (period / 4L))
        if at - state.LastDispatch >= dispatchPeriod then
            state.LastDispatch <- at - (at - state.LastDispatch) % dispatchPeriod
            refillBounded at (double state.Options.MaxPoseFanoutPerTick)
                (double state.Options.MaxPoseFanoutPerTick * 1000.0 / double period) state.FanoutCredit
            let sources =
                if state.Options.Enabled then state.Members.Values |> Seq.filter (fun item -> item.Active && item.Latest.IsSome) |> Seq.toArray
                else [||]
            let mutable budget = state.Options.MaxPoseFanoutPerTick
            let mutable visitedSources = 0
            while visitedSources < sources.Length && budget > 0 && state.FanoutCredit.Available >= 1.0 do
                let current = sources[(state.Cursor + visitedSources) % sources.Length]
                visitedSources <- visitedSources + 1
                let struct (pose, receivedAt) = current.Latest.Value
                let identity = struct (pose.Generation, pose.Sequence.Value)
                let admitted = current.DispatchedPose = Some identity
                refillBounded at 2.0 (1000.0 / double period) current.ReplicationCredit
                if at - receivedAt > int64 state.Options.PoseTimeoutMs then current.Latest <- None
                elif admitted || current.ReplicationCredit.Available >= 1.0 then
                    let mutable sent = false
                    let encoded = Dictionary<uint64, TransportPacket>()
                    // Reverse only the selected Presence-authorized subscriptions;
                    // pose fanout never scans unrelated online sessions.
                    let audience = match state.Audiences.TryGetValue current.Player with true, audience -> Seq.toArray audience | _ -> [||]
                    let mutable visited = 0
                    while visited < audience.Length && budget > 0 && state.FanoutCredit.Available >= 1.0 do
                        let id = audience[(current.PoseCursor + visited) % audience.Length]
                        visited <- visited + 1
                        budget <- budget - 1
                        let observer = state.Members[id]
                        match observer.Selected.TryGetValue current.Player with
                        | true, selected when observer.Active && (selected.SentGeneration <> Some pose.Generation || pose.Sequence.Value > selected.SentSequence) ->
                            let length = PhantomCodec.posePacketSize current.Player selected.Revision pose
                            refill at state.Options.PoseBytesPerSecond observer.OutgoingPoseCredit
                            refill at state.Options.TotalPoseBytesPerSecond state.OutgoingPoseCredit
                            if observer.Outbox.Count > 0 then ()
                            elif state.OutgoingPoseCredit.Available < double length then
                                // End this turn at exhaustion. Scanning every remaining
                                // source would wrap the cursor back to the same prefix,
                                // letting it consume every refill and starve the tail.
                                budget <- 0
                            elif observer.OutgoingPoseCredit.Available >= double length then
                                let packet =
                                    match encoded.TryGetValue selected.Revision with
                                    | true, packet -> packet
                                    | false, _ -> let packet = PhantomCodec.encodePose current.Player selected.Revision pose
                                                  encoded[selected.Revision] <- packet
                                                  packet
                                match state.Send(id, packet) with
                                | Ok () ->
                                    observer.OutgoingPoseCredit.Available <- observer.OutgoingPoseCredit.Available - double length
                                    state.OutgoingPoseCredit.Available <- state.OutgoingPoseCredit.Available - double length
                                    state.FanoutCredit.Available <- state.FanoutCredit.Available - 1.0
                                    sent <- true
                                    selected.SentSequence <- pose.Sequence.Value
                                    selected.SentGeneration <- Some pose.Generation
                                    fanout.Add 1L
                                | Error _ -> ()
                        | _ -> ()
                    current.PoseCursor <- (current.PoseCursor + visited) % max 1 audience.Length
                    if sent && not admitted then
                        current.ReplicationCredit.Available <- current.ReplicationCredit.Available - 1.0
                        current.DispatchedPose <- Some identity
            state.Cursor <- (state.Cursor + visitedSources) % ServerConfig.MaxPeerLimit

    let stop state =
        for id in state.Members.Keys |> Seq.toArray do detach state id
