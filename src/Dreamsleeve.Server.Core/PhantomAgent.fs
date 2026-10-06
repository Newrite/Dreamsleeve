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
    type private Credit = { mutable At: int64; mutable Available: double }
    type private View = { Authority: uint64; Distance: double }
    type private Selected = { Revision: uint64; Asset: PhantomManifest; Authority: uint64; mutable SentSequence: uint64 }
    type private Member = {
        Player: PlayerId; mutable Character: uint64; mutable Context: uint64; mutable Located: bool; mutable Active: bool
        mutable Preferences: PhantomPreferences; Views: Dictionary<PlayerId, View>; Selected: Dictionary<PlayerId, Selected>
        mutable ClearedAuthority: uint64; mutable Revision: uint64; mutable HighManifest: PhantomManifest option
        mutable Ready: PhantomManifest option; mutable Latest: struct (PhantomPose * int64) option
        mutable LastSequence: uint64; mutable NextPublish: int64; mutable PoseCursor: int
        ModelCredit: Credit; PoseCredit: Credit; PoseSamples: Credit; OutgoingPoseCredit: Credit; Commands: Credit; Outbox: Queue<TransportPacket>
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
        mutable Offset: int; mutable Sent: int; mutable Acknowledged: int; mutable Touched: int64; mutable Phase: Phase
    }
    type Snapshot = { Members: int; Sources: int; Subscriptions: int; Transfers: int; LatestPoses: int; PendingIo: int }
    type State = private {
        Options: PhantomOptions; Storage: PhantomStoragePort; Send: Guid * TransportPacket -> Result<unit, string>
        Members: Dictionary<Guid, Member>; Players: Dictionary<PlayerId, Guid>; Transfers: Dictionary<PhantomTransferId, Transfer>
        Audiences: Dictionary<PlayerId, HashSet<Guid>>
        Cleanup: ResizeArray<Task<unit>>; ModelCredit: Credit; OutgoingPoseCredit: Credit; mutable NextTransfer: uint64; mutable LastTick: int64; mutable Cursor: int
    }

    let private credit at rate = { At = at; Available = double rate }
    let private refill at rate (value: Credit) =
        value.Available <- min (double rate) (value.Available + double (max 0L (at - value.At)) * double rate / 1000.0)
        value.At <- at
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
        memberState.Ready <- None
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
                            if observer.Selected.ContainsKey player || count < state.Options.MaxSubscribers then yield player, view.Distance
                        | _ -> ()
            }
            let selection = PhantomPolicy.select observer.Preferences (observer.Selected.Keys |> Set.ofSeq) candidates
            for player in observer.Selected.Keys |> Seq.toArray do
                let selected = observer.Selected[player]
                let retained =
                    selection.Contains player && observer.Views.ContainsKey player
                    && observer.Views[player].Authority = selected.Authority
                if not retained then
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
                        let replacement = { selected with Revision = nextRevision observer; Asset = current.Ready.Value; SentSequence = 0UL }
                        observer.Selected[player] <- replacement
                        emit state id (PhantomResponse.Offer(player, replacement.Revision, replacement.Asset))
                    | _ -> ()
            for player in selection do
                if not (observer.Selected.ContainsKey player) then
                    match source state player with
                    | ValueSome current when current.Ready.IsSome && observer.Views.ContainsKey player ->
                        let selected = { Revision = nextRevision observer; Asset = current.Ready.Value; Authority = observer.Views[player].Authority; SentSequence = 0UL }
                        observer.Selected[player] <- selected
                        let audience =
                            match state.Audiences.TryGetValue player with
                            | true, audience -> audience
                            | _ -> let audience = HashSet<Guid>() in state.Audiences[player] <- audience; audience
                        audience.Add id |> ignore
                        let count = match subscribers.TryGetValue player with true, count -> count | _ -> 0
                        subscribers[player] <- count + 1
                        emit state id (PhantomResponse.Offer(player, selected.Revision, selected.Asset))
                    | _ -> ()

    let private refresh state =
        if state.Options.Enabled then refreshEnabled state

    let create options storage send = {
        Options = options; Storage = storage; Send = send; Members = Dictionary(); Players = Dictionary(); Transfers = Dictionary()
        Cleanup = ResizeArray(); Audiences = Dictionary(); ModelCredit = credit 0L options.ModelBytesPerSecond; NextTransfer = 0UL; LastTick = 0L; Cursor = 0
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
                if characterChanged then
                    clearSource state memberState
                    memberState.LastSequence <- 0UL
                elif contextChanged then
                    memberState.Latest <- None
                    memberState.LastSequence <- 0UL
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
                    Views = Dictionary(); Selected = Dictionary(); ClearedAuthority = 0UL; Revision = 0UL; HighManifest = None
                    Ready = None; Latest = None; LastSequence = 0UL; NextPublish = 0L; PoseCursor = 0
                    ModelCredit = credit 0L options.PlayerModelBytesPerSecond; PoseCredit = credit 0L options.PoseBytesPerSecond
                    OutgoingPoseCredit = credit 0L options.PoseBytesPerSecond; PoseSamples = { At = 0L; Available = 2.0 }
                    Commands = credit 0L options.CommandsPerSecond; Outbox = Queue()
                }
                state.Players[value.Identity.PlayerId] <- id
            | _ -> ()
        | PhantomObservation.View(id, sourceConnection, player, revision, distance) ->
            let mutable observer = Unchecked.defaultof<Member>
            let mutable currentSource = Guid.Empty
            // Presence captures the source session epoch when it projects a view.
            // A queued fact cannot bind an absent or reconnected PlayerId.
            if state.Members.TryGetValue(id, &observer) && observer.Player <> player
               && state.Players.TryGetValue(player, &currentSource) && currentSource = sourceConnection
               && Double.IsFinite distance && distance >= 0.0 then
                let mutable view = Unchecked.defaultof<View>
                if observer.Views.TryGetValue(player, &view) then
                    if revision >= view.Authority && (revision <> view.Authority || distance <> view.Distance) then
                        observer.Views[player] <- { Authority = revision; Distance = distance }
                elif revision > observer.ClearedAuthority then
                    observer.Views[player] <- { Authority = revision; Distance = distance }
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
                                     View = revision; Upload = upload; Offset = 0; Sent = 0; Acknowledged = 0; Touched = at; Phase = phase }

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
                let sources = state.Members.Values |> Seq.filter (fun item -> item.Ready.IsSome) |> Seq.length
                let pendingSources = state.Transfers.Values |> Seq.filter (fun item ->
                    item.Upload && (match source state item.Source with ValueSome current -> current.Ready.IsNone | ValueNone -> false)) |> Seq.length
                let existingSource = memberState.Ready.IsSome || (state.Transfers.Values |> Seq.exists (fun item -> item.Upload && item.Owner = id))
                let stale = memberState.HighManifest |> Option.exists (fun highest ->
                    manifest.Generation.Value < highest.Generation.Value || (manifest.Generation = highest.Generation && manifest <> highest))
                if not memberState.Preferences.Publish || not memberState.Located || context <> memberState.Context
                   || stale then
                    refuse state id memberState.Player manifest.Generation true request 0 "publish admission"
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
                    memberState.HighManifest <- Some manifest
                    memberState.NextPublish <- at + int64 state.Options.PublishCooldownMs
                    startTransfer state at id memberState.Player manifest memberState.Character context 0UL true request
                    refresh state
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
                        transfer.Acknowledged <- offset
                        transfer.Touched <- at
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
                    match PhantomCodec.decodePose state.Options bytes, memberState.Ready with
                    | Ok pose, Some asset when memberState.Located && memberState.Preferences.Publish
                                               && pose.Generation = asset.Generation && pose.Context = memberState.Context
                                               && pose.Sequence.Value > memberState.LastSequence ->
                        memberState.LastSequence <- pose.Sequence.Value
                        memberState.Latest <- Some(struct (pose, at))
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
        if not (valid state transfer) then cancel state transfer.Id false "stale transfer"
        elif at - transfer.Touched > int64 state.Options.TransferTimeoutMs then cancel state transfer.Id false "transfer timeout"
        else
            let ready () =
                let owner = state.Members[transfer.Owner]
                if owner.Ready <> Some transfer.Manifest then
                    owner.LastSequence <- 0UL
                    owner.Latest <- None
                owner.Ready <- Some transfer.Manifest
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
                        refill at state.Options.PlayerModelBytesPerSecond owner.ModelCredit
                        refill at state.Options.ModelBytesPerSecond state.ModelCredit
                        if owner.ModelCredit.Available >= double chunk.Bytes.Length && state.ModelCredit.Available >= double chunk.Bytes.Length then
                            owner.ModelCredit.Available <- owner.ModelCredit.Available - double chunk.Bytes.Length
                            state.ModelCredit.Available <- state.ModelCredit.Available - double chunk.Bytes.Length
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
                            refill at state.Options.PlayerModelBytesPerSecond owner.ModelCredit
                            refill at state.Options.ModelBytesPerSecond state.ModelCredit
                            if owner.ModelCredit.Available < double bytes.Length || state.ModelCredit.Available < double bytes.Length then sending <- false
                            else
                                match state.Send(transfer.Owner, PhantomCodec.encode (PhantomResponse.Chunk(transfer.Id, offset, bytes))) with
                                | Error _ -> sending <- false
                                | Ok () ->
                                    queue.Dequeue() |> ignore
                                    owner.ModelCredit.Available <- owner.ModelCredit.Available - double bytes.Length
                                    state.ModelCredit.Available <- state.ModelCredit.Available - double bytes.Length
                                    transfer.Sent <- transfer.Sent + bytes.Length
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

    let tick state at =
        // Cleanup completions are bounded by admitted transfers, and drained even
        // when publishing is disabled. No file work runs inside this turn.
        state.Cleanup.RemoveAll(Predicate(fun pending -> pending.IsCompleted)) |> ignore
        let transfers = state.Transfers.Values |> Seq.toArray
        for index in 0 .. transfers.Length - 1 do settle state at transfers[(state.Cursor + index) % transfers.Length]
        if at - state.LastTick >= int64 state.Options.ReplicationIntervalMs then
            state.LastTick <- at
            refresh state
            for KeyValue(id, memberState) in state.Members do flushOutbox state id memberState
            let sources =
                if state.Options.Enabled then state.Members.Values |> Seq.filter (fun item -> item.Active && item.Latest.IsSome) |> Seq.toArray
                else [||]
            let mutable budget = state.Options.MaxPoseFanoutPerTick
            let mutable visitedSources = 0
            while visitedSources < sources.Length && budget > 0 do
                let current = sources[(state.Cursor + visitedSources) % sources.Length]
                visitedSources <- visitedSources + 1
                let struct (pose, receivedAt) = current.Latest.Value
                if at - receivedAt <= int64 state.Options.PoseTimeoutMs then
                    let encoded = Dictionary<uint64, TransportPacket>()
                    // Reverse only the selected Presence-authorized subscriptions;
                    // pose fanout never scans unrelated online sessions.
                    let audience = match state.Audiences.TryGetValue current.Player with true, audience -> Seq.toArray audience | _ -> [||]
                    let mutable visited = 0
                    while visited < audience.Length && budget > 0 do
                        let id = audience[(current.PoseCursor + visited) % audience.Length]
                        visited <- visited + 1
                        // Charge inspected subscriptions too, so a blocked peer or
                        // byte budget cannot create an unbounded replication turn.
                        budget <- budget - 1
                        let observer = state.Members[id]
                        match observer.Selected.TryGetValue current.Player with
                        | true, selected when observer.Active && observer.Outbox.Count = 0 && pose.Sequence.Value > selected.SentSequence ->
                            let length = PhantomCodec.posePacketSize current.Player selected.Revision pose
                            refill at state.Options.PoseBytesPerSecond observer.OutgoingPoseCredit
                            refill at state.Options.TotalPoseBytesPerSecond state.OutgoingPoseCredit
                            if state.OutgoingPoseCredit.Available < double length then
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
                                    selected.SentSequence <- pose.Sequence.Value
                                    fanout.Add 1L
                                | Error _ -> ()
                        | _ -> ()
                    current.PoseCursor <- (current.PoseCursor + visited) % max 1 audience.Length
                else current.Latest <- None
            state.Cursor <- (state.Cursor + max 1 visitedSources) % ServerConfig.MaxPeerLimit

    let stop state =
        for id in state.Members.Keys |> Seq.toArray do detach state id
