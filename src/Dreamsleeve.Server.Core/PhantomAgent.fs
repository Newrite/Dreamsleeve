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
            tags.Add("receiver", receiver)
            tags.Add("event", kind)
            tags.Add("player", uint64 player)
            tags.Add("context", context)
            tags.Add("generation", generation)
            tags.Add("view", view)
            tags.Add("reason", reason)
            lifecycle.Add(1L, &tags)
    let private trace kind player context generation view reason = traceFor 0UL kind player context generation view reason
    type private Credit = {
        mutable At: int64
        mutable Available: double
    }

    type private View = {
        Authority: uint64
        Distance: double
        Facing: double option
    }

    type private Selected = {
        Revision: uint64
        Asset: PhantomManifest
        Authority: uint64

        mutable SentSequence: uint64
        mutable SentGeneration: AppearanceGeneration option
        mutable Displayed: AppearanceGeneration option
        mutable DisplayProgressAt: int64
        mutable DisplayProgressBytes: int
    }
    type private Member = {
        Player: PlayerId
        mutable Character: uint64
        mutable Context: uint64
        mutable Located: bool
        mutable Active: bool

        mutable Demand: struct (uint64 * bool) option
        mutable Preferences: PhantomPreferences
        Views: Dictionary<PlayerId, View>
        Selected: Dictionary<PlayerId, Selected>
        mutable ClearedAuthority: uint64
        mutable Revision: uint64
        mutable HighManifest: PhantomManifest option

        mutable Bases: struct (AssetHash option * AssetHash option)
        mutable Ready: PhantomManifest option
        mutable Latest: struct (PhantomPose * int64) option
        mutable Previous: AppearanceGeneration option
        mutable Settled: AppearanceGeneration option
        mutable CommittedAt: int64
        mutable DispatchedPose: struct (AppearanceGeneration * uint64) option
        mutable PreviousSequence: uint64
        mutable LastPose: struct (AppearanceGeneration * uint64) option
        mutable NextPublish: int64
        mutable PoseCursor: int

        PoseCredit: Credit
        PoseSamples: Credit
        ReplicationCredit: Credit
        OutgoingPoseCredit: Credit
        Commands: Credit
        Outbox: Queue<TransportPacket>
    }
    type private Phase =
        | StartingUpload of Task<Result<bool, PhantomStorageError>>
        | StartingDownload of Task<Result<PhantomDelta option, PhantomStorageError>>
        | StreamingHttp of PhantomHttpLease
    type private Transfer = {
        Id: PhantomTransferId
        Request: PhantomRequestId
        Owner: Guid
        Source: PlayerId
        Manifest: PhantomManifest
        Character: uint64
        Context: uint64
        View: uint64
        Upload: bool

        mutable Delta: PhantomDelta option
        mutable Progress: int
        mutable Touched: int64
        mutable Phase: Phase
    }
    type Snapshot = {
        Members: int
        Sources: int
        Subscriptions: int
        Transfers: int
        LatestPoses: int
        PendingIo: int
    }
    type State = private {
        Options: PhantomOptions
        Storage: PhantomStoragePort
        Http: PhantomHttpPort
        Send: Guid * TransportPacket -> Result<unit, TransportSendError>

        Members: Dictionary<Guid, Member>
        Players: Dictionary<PlayerId, Guid>
        Transfers: Dictionary<PhantomTransferId, Transfer>
        Audiences: Dictionary<PlayerId, HashSet<Guid>>
        Cleanup: ResizeArray<Task<Result<unit, exn>>>

        OutgoingPoseCredit: Credit
        mutable NextTransfer: uint64
        mutable LastTick: int64
        mutable LastDispatch: int64
        FanoutCredit: Credit
        mutable Cursor: int
    }

    let private credit at rate = {
        At = at
        Available = double rate
    }

    let private refillBounded at capacity rate (value: Credit) =
        value.Available <- min capacity (value.Available + double (max 0L (at - value.At)) * rate / 1000.0)
        value.At <- at
    let private refill at rate value = refillBounded at (double rate) (double rate) value
    let private take at rate amount value =
        refill at rate value
        if value.Available < double amount then false
        else
            value.Available <- value.Available - double amount
            true
    let private takeCommand at rate value =
        refill at rate value
        value.Available <- min 8.0 value.Available
        if value.Available < 1.0 then false
        else
            value.Available <- value.Available - 1.0
            true
    let private deny () = rejected.Add 1L
    let private nextRevision (memberState: Member) =
        memberState.Revision <- memberState.Revision + 1UL
        memberState.Revision
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
    type private Completion =
        | Accepted
        | SourceChanged
        | ContextChanged
        | ViewRemoved
        | Disconnected
        | Cancelled
        | StaleTransfer
        | Superseded
        | TransferTimeout
        | StorageFailure of PhantomStorageError
        | HttpFailure of PhantomHttpError

    let private completion = function
        | Accepted -> true, "", false
        | SourceChanged -> false, "source changed", false
        | ContextChanged -> false, "context changed", false
        | ViewRemoved -> false, "view removed", false
        | Disconnected -> false, "disconnected", false
        | Cancelled -> false, "cancelled", false
        | StaleTransfer -> false, "stale transfer", false
        | Superseded -> false, "superseded", false
        | TransferTimeout -> false, "transfer timeout", true
        | StorageFailure error -> false, PhantomStorageError.message error, PhantomStorageError.retryable error
        | HttpFailure error -> false, PhantomHttpError.message error, PhantomHttpError.retryable error

    // A cleanup boundary observes faults while still trying each ownership release.
    // The result is lifecycle failure, never a successful command rejection.
    let private releaseTransfer state (transfer: Transfer) =
        // Detach immutable handles/task identity before leaving the actor turn.
        let http, storage, id = state.Http, state.Storage, transfer.Id
        let pending: Task option =
            match transfer.Phase with
            | StartingUpload work -> Some work
            | StartingDownload work -> Some work
            | StreamingHttp _ -> None
        task {
            let mutable first = None
            let observe (operation: unit -> Task) = task {
                try do! operation()
                with error -> if first.IsNone then first <- Some error
            }
            match pending with
            | Some work -> do! observe (fun () -> work)
            | None -> ()
            do! observe (fun () -> http.Cancel id)
            do! observe (fun () -> storage.Cancel id)
            return
                match first with
                | Some error -> Error error
                | None -> Ok ()
        }

    let failure state =
        let mutable failed = None
        if state.Storage.OwnerFailure.IsCompletedSuccessfully then failed <- Some state.Storage.OwnerFailure.Result
        elif state.Http.OwnerFailure.IsCompletedSuccessfully then failed <- Some state.Http.OwnerFailure.Result
        for cleanup in state.Cleanup do
            if cleanup.IsFaulted then
                let observed = cleanup.Exception.GetBaseException()
                if failed.IsNone then failed <- Some observed
            elif failed.IsNone && cleanup.IsCanceled then failed <- Some (Threading.Tasks.TaskCanceledException(cleanup))
            elif failed.IsNone && cleanup.IsCompletedSuccessfully then
                match cleanup.Result with
                | Error error -> failed <- Some error
                | Ok () -> ()
        for transfer in state.Transfers.Values do
            let work: Task =
                match transfer.Phase with
                | StartingUpload pending -> pending
                | StartingDownload pending -> pending
                | StreamingHttp lease -> lease.Completion
            if work.IsFaulted then
                let observed = work.Exception.GetBaseException()
                if failed.IsNone then failed <- Some observed
            elif failed.IsNone && work.IsCanceled then failed <- Some (Threading.Tasks.TaskCanceledException(work))
        failed

    let private completedCleanup (pending: Task<Result<unit, exn>>) =
        pending.IsCompletedSuccessfully && Result.isOk pending.Result

    let private cancel state id outcome =
        match state.Transfers.TryGetValue id with
        | true, transfer ->
            let accepted, reason, retryable = completion outcome
            trace (if accepted then "transfer_complete" else "transfer_cancel") transfer.Source transfer.Context transfer.Manifest.Generation.Value transfer.View reason
            state.Transfers.Remove id |> ignore
            state.Cleanup.Add(releaseTransfer state transfer)
            let completion = {
                Target = {
                    Player = transfer.Source
                    Generation = transfer.Manifest.Generation
                }
                Upload = transfer.Upload
                RetryAfterMs = if retryable then (if transfer.Upload then max 1 state.Options.PublishCooldownMs else 1000) else 0
            }
            let reason = if Text.Encoding.UTF8.GetByteCount reason <= 256 then reason else "storage failure"
            let responseId =
                match transfer.Phase with
                | StartingUpload _ | StartingDownload _ -> PhantomTransferId 0UL
                | _ -> id
            emit state transfer.Owner (PhantomResponse.Complete(responseId, accepted, reason, transfer.Request, Some completion))
        | _ -> ()
    let private clearSource state (memberState: Member) =
        trace "source_clear" memberState.Player memberState.Context (memberState.Ready |> Option.map (fun x -> x.Generation.Value) |> Option.defaultValue 0UL) 0UL ""
        memberState.Ready <- None
        memberState.Previous <- None
        memberState.PreviousSequence <- 0UL
        memberState.Settled <- None
        memberState.Latest <- None
        let affected = state.Transfers.Values |> Seq.filter (fun item -> item.Source = memberState.Player) |> Seq.map _.Id |> Seq.toArray
        for id in affected do cancel state id SourceChanged
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
                        cancel state transfer.Id ViewRemoved
                else
                    match source state player with
                    | ValueSome current when current.Ready.IsSome && current.Ready.Value <> selected.Asset ->
                        // An appearance replacement is not an AOI departure. Keep
                        // the receiver's previous scene until this offer is ready.
                        for transfer in state.Transfers.Values |> Seq.filter (fun item -> not item.Upload && item.Owner = id && item.Source = player) |> Seq.toArray do
                            cancel state transfer.Id Superseded
                        let replacement = {
                            selected with
                                Revision = nextRevision observer
                                Asset = current.Ready.Value
                                SentSequence = 0UL
                                SentGeneration = None
                                DisplayProgressAt = max current.CommittedAt state.LastTick
                                DisplayProgressBytes = 0
                        }
                        observer.Selected[player] <- replacement
                        traceFor (uint64 observer.Player) "offer" player current.Context replacement.Asset.Generation.Value replacement.Revision "replacement"
                        emit state id (PhantomResponse.Offer(player, replacement.Revision, replacement.Asset))
                    | _ -> ()
            for player in selection do
                if not (observer.Selected.ContainsKey player) then
                    match source state player with
                    | ValueSome current when current.Ready.IsSome && observer.Views.ContainsKey player ->
                        let selected = {
                            Revision = nextRevision observer
                            Asset = current.Ready.Value
                            Authority = observer.Views[player].Authority

                            SentSequence = 0UL
                            SentGeneration = None
                            Displayed = None
                            DisplayProgressAt = max current.CommittedAt state.LastTick
                            DisplayProgressBytes = 0
                        }
                        observer.Selected[player] <- selected
                        let audience =
                            match state.Audiences.TryGetValue player with
                            | true, audience -> audience
                            | _ ->
                                let audience = HashSet<Guid>()
                                state.Audiences[player] <- audience
                                audience
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

    let create options storage http send = {
        Options = options
        Storage = storage
        Http = http
        Send = send

        Members = Dictionary()
        Players = Dictionary()
        Transfers = Dictionary()
        Cleanup = ResizeArray()
        Audiences = Dictionary()

        NextTransfer = 0UL
        LastTick = 0L
        LastDispatch = 0L
        Cursor = 0
        FanoutCredit = credit 0L options.MaxPoseFanoutPerTick
        OutgoingPoseCredit = credit 0L options.TotalPoseBytesPerSecond
    }
    let observationMode state =
        if state.Options.Enabled then PhantomObservationMode.Full else PhantomObservationMode.Membership
    let snapshot state = {
        Members = state.Members.Count
        Sources = state.Members.Values |> Seq.filter (fun item -> item.Ready.IsSome) |> Seq.length
        Subscriptions = state.Members.Values |> Seq.sumBy (fun item -> item.Selected.Count)
        Transfers = state.Transfers.Count
        LatestPoses = state.Members.Values |> Seq.filter (fun item -> item.Latest.IsSome) |> Seq.length
        PendingIo = state.Transfers.Count
    }
    let detach state id =
        match state.Members.TryGetValue id with
        | true, memberState ->
            clearSource state memberState
            for transfer in state.Transfers.Values |> Seq.filter (fun item -> item.Owner = id) |> Seq.toArray do cancel state transfer.Id Disconnected
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
                    memberState.Bases <- struct (None, None)
                    clearSource state memberState
                    memberState.LastPose <- None
                    memberState.DispatchedPose <- None
                elif contextChanged then
                    memberState.Latest <- None
                    memberState.LastPose <- None
                    memberState.DispatchedPose <- None
                    memberState.Previous <- None
                    memberState.PreviousSequence <- 0UL
                    for transfer in state.Transfers.Values |> Seq.filter (fun item -> item.Source = memberState.Player) |> Seq.toArray do
                        cancel state transfer.Id ContextChanged
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
                    Player = value.Identity.PlayerId
                    Character = value.CharacterGeneration
                    Context = value.MovementContext
                    Located = value.Location.IsSome && value.MovementContext <> 0UL
                    Active = false

                    Preferences = {
                        Publish = options.Enabled
                        Receive = options.Enabled
                        Maximum = options.Maximum
                        Distance = options.Distance
                    }
                    Demand = None
                    Views = Dictionary()
                    Selected = Dictionary()
                    ClearedAuthority = 0UL
                    Revision = 0UL
                    HighManifest = None

                    Bases = struct (None, None)
                    Ready = None
                    Latest = None
                    Previous = None
                    Settled = None
                    CommittedAt = 0L
                    PreviousSequence = 0UL
                    LastPose = None
                    DispatchedPose = None
                    NextPublish = 0L
                    PoseCursor = 0

                    PoseCredit = credit 0L options.PoseBytesPerSecond
                    OutgoingPoseCredit = credit 0L options.PoseBytesPerSecond
                    PoseSamples = credit 0L 2
                    ReplicationCredit = credit 0L 2
                    Commands = credit 0L options.CommandsPerSecond
                    Outbox = Queue()
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
                        observer.Views[player] <- {
                            Authority = revision
                            Distance = distance
                            Facing = facing
                        }
                elif revision > observer.ClearedAuthority then
                    observer.Views[player] <- {
                        Authority = revision
                        Distance = distance
                        Facing = facing
                    }
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
        state.Cleanup.RemoveAll(Predicate(completedCleanup)) |> ignore
        let count = state.Transfers.Values |> Seq.filter (fun item -> item.Owner = owner) |> Seq.length
        state.Transfers.Count + state.Cleanup.Count < state.Options.MaxTransfers && count < state.Options.TransfersPerPlayer

    let private refuse state owner player generation upload request retry reason =
        let completion = {
            Target = {
                Player = player
                Generation = generation
            }
            Upload = upload
            RetryAfterMs = retry
        }
        emit state owner (PhantomResponse.Complete(PhantomTransferId 0UL, false, reason, request, Some completion))
        deny()

    let private startTransfer state at owner player (manifest: PhantomManifest) character context revision upload request change basis =
        if not (transferAvailable state owner) then
            refuse state owner player manifest.Generation upload request (if upload then max 1 state.Options.PublishCooldownMs else 1000) "transfer limit"
        else
            state.NextTransfer <- state.NextTransfer + 1UL
            let id = PhantomTransferId state.NextTransfer
            let phase = if upload then StartingUpload(state.Storage.StartUpload(id, manifest, change)) else StartingDownload(state.Storage.StartDownload(id, manifest, basis))
            state.Transfers[id] <- {
                Id = id
                Request = request
                Owner = owner
                Source = player
                Manifest = manifest
                Character = character
                Context = context
                View = revision
                Upload = upload

                Delta = change
                Progress = 0
                Touched = at
                Phase = phase
            }

    // Keep a cold receiver's chosen base stable until its native scene is ready.
    // The existing progress timeout bounds a missing Displayed acknowledgement.
    let private awaitingFirstDisplay state at player =
        match state.Audiences.TryGetValue player with
        | true, audience ->
            audience |> Seq.exists (fun id ->
                match state.Members[id].Selected.TryGetValue player with
                | true, selected ->
                    selected.Displayed.IsNone && at - selected.DisplayProgressAt < int64 state.Options.TransferTimeoutMs
                    && (selected.DisplayProgressBytes > 0 || (state.Transfers.Values |> Seq.exists (fun transfer -> not transfer.Upload && transfer.Owner = id && transfer.Source = player)))
                | _ -> false)
        | _ -> false

    let handle state at id request =
        match state.Members.TryGetValue id with
        | true, memberState when memberState.Active && state.Options.Enabled ->
            match request with
            | PhantomRequest.Preferences preferences ->
                memberState.Preferences <- PhantomPolicy.effective state.Options.Maximum state.Options.Distance preferences
                if not preferences.Publish then clearSource state memberState
                refresh state
            | PhantomRequest.Withdraw ->
                clearSource state memberState
                refresh state
            | PhantomRequest.Publish(manifest, context, request, change) ->
                trace "publish_request" memberState.Player context manifest.Generation.Value 0UL ""
                let sources = state.Members.Values |> Seq.filter (fun item -> item.Ready.IsSome) |> Seq.length
                let pendingSources = state.Transfers.Values |> Seq.filter (fun item ->
                    item.Upload && (match source state item.Source with ValueSome current -> current.Ready.IsNone | ValueNone -> false)) |> Seq.length
                let existingSource = memberState.Ready.IsSome || (state.Transfers.Values |> Seq.exists (fun item -> item.Upload && item.Owner = id))
                let stale = memberState.HighManifest |> Option.exists (fun highest ->
                    manifest.Generation.Value < highest.Generation.Value || (manifest.Generation = highest.Generation && manifest <> highest))
                let struct (basis, priorBasis) = memberState.Bases
                if (change |> Option.exists (fun d -> basis <> Some d.BaseHash && priorBasis <> Some d.BaseHash)) then
                    refuse state id memberState.Player manifest.Generation true request 0 "delta base unavailable"
                elif not memberState.Preferences.Publish || not memberState.Located || context <> memberState.Context
                   || stale then
                    refuse state id memberState.Player manifest.Generation true request 0 "publish admission"
                elif memberState.Ready |> Option.exists (fun ready -> memberState.Settled <> Some ready.Generation && manifest <> ready) then
                    refuse state id memberState.Player manifest.Generation true request 1000 "replacement pending"
                elif (memberState.Ready |> Option.exists (fun ready -> manifest <> ready)) && awaitingFirstDisplay state at memberState.Player then
                    refuse state id memberState.Player manifest.Generation true request 1000 "initial display pending"
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
                        cancel state transfer.Id Superseded
                    memberState.Previous <- memberState.Ready |> Option.map _.Generation
                    match memberState.LastPose with
                    | Some struct (generation, sequence) when memberState.Previous = Some generation -> memberState.PreviousSequence <- sequence
                    | _ -> ()
                    memberState.HighManifest <- Some manifest
                    memberState.NextPublish <- at + int64 state.Options.PublishCooldownMs
                    startTransfer state at id memberState.Player manifest memberState.Character context 0UL true request change None
                    refresh state
            | PhantomRequest.Displayed(player, revision, generation) ->
                match memberState.Selected.TryGetValue player with
                | true, selected when selected.Revision = revision && selected.Asset.Generation = generation ->
                    traceFor (uint64 memberState.Player) "displayed" player memberState.Context generation.Value revision ""
                    selected.Displayed <- Some generation
                | _ -> deny()
            | PhantomRequest.Download(player, generation, request, basis) ->
                match memberState.Selected.TryGetValue player, source state player with
                | (true, selected), ValueSome current when selected.Asset.Generation = generation ->
                    let previous = state.Transfers.Values |> Seq.filter (fun item -> item.Owner = id && not item.Upload && item.Source = player) |> Seq.toArray
                    if previous |> Array.forall (fun item -> item.Request <> request) then
                        for transfer in previous do cancel state transfer.Id Superseded
                        startTransfer state at id player selected.Asset current.Character current.Context selected.Revision false request None basis
                | _ -> refuse state id player generation false request 1000 "view unavailable"
            | PhantomRequest.Cancel transferId ->
                match state.Transfers.TryGetValue transferId with
                | true, transfer when transfer.Owner = id -> cancel state transferId Cancelled
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
                        let advancing =
                            match memberState.LastPose with
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
            | Ok () ->
                memberState.Outbox.Dequeue() |> ignore
                count <- count + 1
            | Error _ -> sending <- false

    let private settle state at (transfer: Transfer) =
        if not (valid state transfer) then cancel state transfer.Id StaleTransfer
        elif at - transfer.Touched > int64 state.Options.TransferTimeoutMs then cancel state transfer.Id TransferTimeout
        else
            let ready () =
                let owner = state.Members[transfer.Owner]
                if owner.Ready <> Some transfer.Manifest then
                    // A pose for the pending generation can already be admitted.
                    // Commit must not reopen its sequence floor or discard it.
                    owner.Latest <- owner.Latest |> Option.filter (fun struct (pose, _) -> pose.Generation = transfer.Manifest.Generation)
                trace "committed" owner.Player owner.Context transfer.Manifest.Generation.Value 0UL ""
                let struct (basis, _) = owner.Bases
                if basis <> Some transfer.Manifest.Hash then owner.Bases <- struct (Some transfer.Manifest.Hash, basis)
                owner.Ready <- Some transfer.Manifest
                owner.CommittedAt <- at
                cancel state transfer.Id Accepted
            let beginHttp () =
                let lease = state.Http.Admit(transfer.Owner, transfer.Id, transfer.Manifest, transfer.Upload, transfer.Delta)
                transfer.Phase <- StreamingHttp lease
                emit state transfer.Owner (PhantomResponse.Transfer(transfer.Id, transfer.Manifest, transfer.Source, transfer.Upload, transfer.Request, lease.Token, transfer.Delta))
            match transfer.Phase with
            | StartingUpload pending when pending.IsCompletedSuccessfully ->
                match pending.Result with
                | Error reason -> cancel state transfer.Id (StorageFailure reason)
                | Ok true -> ready()
                | Ok false -> beginHttp()
            | StartingDownload pending when pending.IsCompletedSuccessfully ->
                match pending.Result with
                | Error reason -> cancel state transfer.Id (StorageFailure reason)
                | Ok change ->
                    transfer.Delta <- change
                    beginHttp()
            | StreamingHttp lease ->
                let progress = lease.Progress
                if progress > transfer.Progress then
                    let count = int64 (progress - transfer.Progress)
                    if transfer.Upload then uploaded.Add count else downloaded.Add count
                    transfer.Progress <- progress
                    transfer.Touched <- at
                    if not transfer.Upload then
                        match state.Members[transfer.Owner].Selected.TryGetValue transfer.Source with
                        | true, selected when selected.Revision = transfer.View && progress > selected.DisplayProgressBytes ->
                            selected.DisplayProgressBytes <- progress
                            selected.DisplayProgressAt <- at
                        | _ -> ()
                if lease.Completion.IsCompletedSuccessfully then
                    match lease.Completion.Result with
                    | Error reason -> cancel state transfer.Id (HttpFailure reason)
                    | Ok () when transfer.Upload -> ready()
                    | Ok () -> cancel state transfer.Id Accepted
            | StartingUpload _ | StartingDownload _ -> ()

    let private tickActive state at =
        // Cleanup completions are bounded by admitted transfers, and drained even
        // when publishing is disabled. No file work runs inside this turn.
        state.Cleanup.RemoveAll(Predicate(completedCleanup)) |> ignore
        let transfers = state.Transfers.Values |> Seq.toArray
        for transfer in transfers do settle state at transfer
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
                        owner.Previous <- None
                        owner.PreviousSequence <- 0UL
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
                    let audience =
                        match state.Audiences.TryGetValue current.Player with
                        | true, audience -> Seq.toArray audience
                        | _ -> [||]
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
                                    | false, _ ->
                                        let packet = PhantomCodec.encodePose current.Player selected.Revision pose
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

    let tick state at =
        if (failure state).IsNone then tickActive state at

    let stop state =
        for id in state.Members.Keys |> Seq.toArray do detach state id
