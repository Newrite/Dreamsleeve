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

    // Commands one member may have admitted ahead of the configured rate.
    let private commandBurst = 8.0
    // Poses a source may send, and distinct poses it may replicate, ahead of their interval.
    let private poseBurst = 2.0
    // A command runs only while its responses can join a short reliable backlog.
    let private commandResponseBacklog = 8
    // Reliable responses sent to one member per turn.
    let private responsesPerFlush = 8
    // Pose fanout runs in turns of a quarter replication interval, never longer than this.
    let private maxDispatchPeriodMs = 25L
    // A refused download, or a publish waiting for observers, is retried after this delay.
    let private retryDelayMs = 1000
    // A refusal the same request cannot overcome.
    let private noRetry = 0
    // Client codecs reject a longer completion reason.
    let private maxReasonBytes = 256

    // A refused upload is retried after the publish cooldown, never immediately.
    let private publishRetryMs (options: PhantomOptions) = max 1 options.PublishCooldownMs

    let private transferRetryMs options upload = if upload then publishRetryMs options else retryDelayMs

    // Responses one member may have queued: an Offer and a Remove for each
    // phantom it shows, four responses per concurrent transfer and a margin
    // for policy, demand and settlement.
    let private outboxCapacity (options: PhantomOptions) = 2 * options.Maximum + 4 * options.TransfersPerPlayer + 16

    /// A token bucket: units accrue with time up to a burst capacity.
    type private Credit = {
        mutable At: int64
        mutable Available: double
    }

    [<RequireQualifiedAccess>]
    module private Credit =
        let create at amount = {
            At = at
            Available = amount
        }

        /// Accrues `perSecond` units for the milliseconds since the last refill, up to `capacity`.
        let refill at capacity perSecond (credit: Credit) =
            credit.Available <- min capacity (credit.Available + double (max 0L (at - credit.At)) * perSecond / 1000.0)
            credit.At <- at

        /// One second of `rate` is the burst.
        let refillSecond at (rate: int) credit = refill at (double rate) (double rate) credit

        let spend amount (credit: Credit) = credit.Available <- credit.Available - amount

        let trySpend amount (credit: Credit) =
            if credit.Available < amount then false
            else
                spend amount credit
                true

        let tryTake at rate (amount: int) credit =
            refillSecond at rate credit
            trySpend (double amount) credit

    type private View = {
        Authority: uint64
        DistanceSquared: double
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

    /// The member's own model as a source.
    type private Publication = {
        /// The newest manifest the member asked to publish; nothing older is admitted.
        mutable Highest: PhantomManifest option
        /// The current and prior committed hashes a delta may build on.
        mutable Bases: struct (AssetHash option * AssetHash option)
        /// The committed model observers are offered.
        mutable Ready: PhantomManifest option
        /// The generation a pending replacement retires; its poses bridge meanwhile.
        mutable Previous: AppearanceGeneration option
        /// The Ready generation no observer still waits to display.
        mutable Settled: AppearanceGeneration option
        mutable CommittedAt: int64
        mutable NextPublish: int64
    }

    /// The member's own poses and what fanout already sent.
    type private PoseStream = {
        mutable Latest: struct (PhantomPose * int64) option
        /// The sequence floor: the newest admitted generation and sequence.
        mutable Last: struct (AppearanceGeneration * uint64) option
        /// The sequence floor of Publication.Previous.
        mutable PreviousSequence: uint64
        /// The pose last charged to the replication rate.
        mutable Dispatched: struct (AppearanceGeneration * uint64) option
        /// Where the next fanout turn starts in the audience.
        mutable AudienceCursor: int
    }

    type private Rates = {
        /// Received pose bytes.
        PoseBytes: Credit
        /// Received poses per sample interval.
        PoseSamples: Credit
        /// Distinct poses of this source replicated per replication interval.
        Replication: Credit
        /// Pose bytes sent to this member as an observer.
        OutgoingPoseBytes: Credit
        /// Reliable commands.
        Commands: Credit
    }

    type private Member = {
        Player: PlayerId
        mutable Character: uint64
        mutable Context: uint64
        mutable Located: bool
        mutable Active: bool

        mutable Preferences: PhantomPreferences
        /// The pose demand last sent to this member.
        mutable Demand: struct (uint64 * bool) option
        Publication: Publication
        Poses: PoseStream

        // This member as an observer of others.
        Views: Dictionary<PlayerId, View>
        Selected: Dictionary<PlayerId, Selected>
        mutable ClearedAuthority: uint64
        mutable Revision: uint64

        Rates: Rates
        // Reliable requests wait here, in arrival order, while responses are
        // backlogged or the command rate is spent; they are never dropped.
        PendingCommands: Queue<PhantomRequest>
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

    let private deny () = rejected.Add 1L

    let private nextRevision (memberState: Member) =
        memberState.Revision <- memberState.Revision + 1UL
        memberState.Revision

    let private emit state id response =
        match state.Members.TryGetValue id with
        | true, memberState when memberState.Active ->
            if memberState.Outbox.Count < outboxCapacity state.Options then
                memberState.Outbox.Enqueue(PhantomCodec.encode response)
            else
                // A bounded lifecycle backlog must not accumulate binary assets.
                memberState.Active <- false
                memberState.Poses.Latest <- None
                deny()
        | _ -> ()

    let private source state player =
        let mutable id = Guid.Empty
        let mutable memberState = Unchecked.defaultof<Member>
        if state.Players.TryGetValue(player, &id) && state.Members.TryGetValue(id, &memberState) then ValueSome memberState
        else ValueNone

    let private downloadOf owner player (transfer: Transfer) =
        not transfer.Upload && transfer.Owner = owner && transfer.Source = player

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

    // The outcome the client acts on, a log text and whether a retry may succeed.
    let private completion = function
        | Accepted -> PhantomOutcome.Accepted, "", false
        | SourceChanged -> PhantomOutcome.SourceChanged, "source changed", false
        | ContextChanged -> PhantomOutcome.ContextChanged, "context changed", false
        | ViewRemoved -> PhantomOutcome.ViewRemoved, "view removed", false
        | Disconnected -> PhantomOutcome.Disconnected, "disconnected", false
        | Cancelled -> PhantomOutcome.Cancelled, "cancelled", false
        | StaleTransfer -> PhantomOutcome.Stale, "stale transfer", false
        | Superseded -> PhantomOutcome.Superseded, "superseded", false
        | TransferTimeout -> PhantomOutcome.Timeout, "transfer timeout", true
        | StorageFailure error ->
            let outcome =
                match error with
                | PhantomStorageError.DeltaBaseUnavailable -> PhantomOutcome.DeltaBaseUnavailable
                | PhantomStorageError.Closed | PhantomStorageError.QueueFull | PhantomStorageError.TransferLimit
                | PhantomStorageError.CacheHashMismatch | PhantomStorageError.AssetUnavailable | PhantomStorageError.DiskQuota
                | PhantomStorageError.HashMismatch | PhantomStorageError.HashSizeConflict | PhantomStorageError.DeltaHashMismatch
                | PhantomStorageError.DeltaReconstruction | PhantomStorageError.DeltaTargetHash
                | PhantomStorageError.UnknownUpload | PhantomStorageError.UploadClosed | PhantomStorageError.ChunkOffsetOrSize
                | PhantomStorageError.ReadBounds | PhantomStorageError.UnknownDownload | PhantomStorageError.DownloadClosed
                | PhantomStorageError.CacheSizeChanged | PhantomStorageError.CacheTruncated | PhantomStorageError.Io _ ->
                    PhantomOutcome.StorageFailure
            outcome, PhantomStorageError.message error, PhantomStorageError.retryable error
        | HttpFailure error -> PhantomOutcome.HttpFailure, PhantomHttpError.message error, PhantomHttpError.retryable error

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
            let outcome, reason, retryable = completion outcome
            trace (if outcome = PhantomOutcome.Accepted then "transfer_complete" else "transfer_cancel") transfer.Source transfer.Context transfer.Manifest.Generation.Value transfer.View reason
            state.Transfers.Remove id |> ignore
            state.Cleanup.Add(releaseTransfer state transfer)
            let completion = {
                Target = {
                    Player = transfer.Source
                    Generation = transfer.Manifest.Generation
                }
                Upload = transfer.Upload
                RetryAfterMs = if retryable then transferRetryMs state.Options transfer.Upload else noRetry
            }
            let reason = if Text.Encoding.UTF8.GetByteCount reason <= maxReasonBytes then reason else "storage failure"
            // The client learns a transfer id only once HTTP streaming begins.
            let responseId =
                match transfer.Phase with
                | StartingUpload _ | StartingDownload _ -> PhantomTransferId 0UL
                | StreamingHttp _ -> id
            emit state transfer.Owner (PhantomResponse.Complete(responseId, outcome, reason, transfer.Request, Some completion))
        | _ -> ()

    let private cancelWhere state outcome (affected: Transfer -> bool) =
        for id in state.Transfers.Values |> Seq.filter affected |> Seq.map _.Id |> Seq.toArray do
            cancel state id outcome

    let private clearSource state (memberState: Member) =
        let publication, poses = memberState.Publication, memberState.Poses
        trace "source_clear" memberState.Player memberState.Context (publication.Ready |> Option.map (fun x -> x.Generation.Value) |> Option.defaultValue 0UL) 0UL ""
        publication.Ready <- None
        publication.Previous <- None
        poses.PreviousSequence <- 0UL
        publication.Settled <- None
        poses.Latest <- None
        cancelWhere state SourceChanged (fun transfer -> transfer.Source = memberState.Player)

    let private valid state (transfer: Transfer) =
        match state.Members.TryGetValue transfer.Owner, source state transfer.Source with
        | (true, owner), ValueSome current when owner.Active && current.Active && current.Located
                                         && current.Character = transfer.Character && current.Context = transfer.Context ->
            if transfer.Upload then current.Preferences.Publish && current.Publication.Highest = Some transfer.Manifest
            else
                match owner.Selected.TryGetValue transfer.Source with
                | true, selected -> selected.Revision = transfer.View && selected.Asset = transfer.Manifest
                | _ -> false
        | _ -> false

    let private subscribe state id player =
        match state.Audiences.TryGetValue player with
        | true, audience -> audience.Add id |> ignore
        | _ -> state.Audiences[player] <- HashSet<Guid>([ id ])

    let private unsubscribe state id player =
        match state.Audiences.TryGetValue player with
        | true, audience ->
            audience.Remove id |> ignore
            if audience.Count = 0 then state.Audiences.Remove player |> ignore
        | _ -> ()

    let private countOf (counts: Dictionary<PlayerId, int>) player =
        match counts.TryGetValue player with
        | true, count -> count
        | _ -> 0

    // Observers subscribed to each source.
    let private countSubscribers state =
        let subscribers = Dictionary<PlayerId, int>()
        for observer in state.Members.Values do
            for player in observer.Selected.Keys do
                subscribers[player] <- countOf subscribers player + 1
        subscribers

    // The sources each observer downloads a model of.
    let private downloadsByObserver state =
        let downloading = Dictionary<Guid, Set<PlayerId>>()
        for transfer in state.Transfers.Values do
            if not transfer.Upload then
                let current = match downloading.TryGetValue transfer.Owner with true, sources -> sources | _ -> Set.empty
                downloading[transfer.Owner] <- current.Add transfer.Source
        downloading

    // Sources the observer may be offered, with their squared distances.
    // Camera culling chooses what to start, never what to abandon: a model
    // download in flight keeps its subscription while the source stays in
    // the observer's AOI and range. A restarted download begins at zero.
    let private candidates state (observer: Member) subscribers (transferring: Set<PlayerId>) = seq {
        if observer.Active && observer.Preferences.Receive && observer.Preferences.Maximum > 0 then
            for KeyValue(player, view) in observer.Views do
                match source state player with
                | ValueSome current when current.Active && current.Located && current.Preferences.Publish && current.Publication.Ready.IsSome ->
                    let retained = observer.Selected.ContainsKey player
                    let culled =
                        state.Options.CameraCulling
                        && not (retained && transferring.Contains player)
                        && not (PhantomPolicy.inView retained view.DistanceSquared view.Facing)
                    if (retained || countOf subscribers player < state.Options.MaxSubscribers) && not culled then
                        yield player, view.DistanceSquared
                | _ -> ()
    }

    let private removeView state id (observer: Member) player (selected: Selected) (subscribers: Dictionary<PlayerId, int>) =
        let reason =
            match observer.Views.TryGetValue player with
            | false, _ -> "aoi-departure"
            | true, view when view.Authority <> selected.Authority -> "authority-changed"
            | true, _ -> "policy-selection"
        traceFor (uint64 observer.Player) "view_removed" player observer.Context selected.Asset.Generation.Value selected.Revision reason
        observer.Selected.Remove player |> ignore
        unsubscribe state id player
        subscribers[player] <- subscribers[player] - 1
        emit state id (PhantomResponse.Remove(player, nextRevision observer))
        cancelWhere state ViewRemoved (downloadOf id player)

    // An appearance replacement is not an AOI departure. Keep the receiver's
    // previous scene until this offer is ready.
    let private reoffer state id (observer: Member) player (selected: Selected) (current: Member) (ready: PhantomManifest) =
        cancelWhere state Superseded (downloadOf id player)
        let replacement = {
            selected with
                Revision = nextRevision observer
                Asset = ready
                SentSequence = 0UL
                SentGeneration = None
                DisplayProgressAt = max current.Publication.CommittedAt state.LastTick
                DisplayProgressBytes = 0
        }
        observer.Selected[player] <- replacement
        traceFor (uint64 observer.Player) "offer" player current.Context replacement.Asset.Generation.Value replacement.Revision "replacement"
        emit state id (PhantomResponse.Offer(player, replacement.Revision, replacement.Asset))

    let private offer state id (observer: Member) player (current: Member) (ready: PhantomManifest) (view: View) (subscribers: Dictionary<PlayerId, int>) =
        let selected = {
            Revision = nextRevision observer
            Asset = ready
            Authority = view.Authority

            SentSequence = 0UL
            SentGeneration = None
            Displayed = None
            DisplayProgressAt = max current.Publication.CommittedAt state.LastTick
            DisplayProgressBytes = 0
        }
        observer.Selected[player] <- selected
        subscribe state id player
        subscribers[player] <- countOf subscribers player + 1
        traceFor (uint64 observer.Player) "offer" player current.Context selected.Asset.Generation.Value selected.Revision "subscription"
        emit state id (PhantomResponse.Offer(player, selected.Revision, selected.Asset))

    let private refreshEnabled state =
        let subscribers = countSubscribers state
        let downloading = downloadsByObserver state
        for KeyValue(id, observer) in state.Members do
            let transferring = match downloading.TryGetValue id with true, sources -> sources | _ -> Set.empty
            let selection =
                PhantomPolicy.select observer.Preferences (observer.Selected.Keys |> Set.ofSeq) transferring
                    (candidates state observer subscribers transferring)
            for player in observer.Selected.Keys |> Seq.toArray do
                let selected = observer.Selected[player]
                let retained =
                    selection.Contains player
                    && (match observer.Views.TryGetValue player with
                        | true, view -> view.Authority = selected.Authority
                        | _ -> false)
                if not retained then removeView state id observer player selected subscribers
                else
                    match source state player with
                    | ValueSome current ->
                        match current.Publication.Ready with
                        | Some ready when ready <> selected.Asset -> reoffer state id observer player selected current ready
                        | _ -> ()
                    | ValueNone -> ()
            for player in selection do
                if not (observer.Selected.ContainsKey player) then
                    match source state player, observer.Views.TryGetValue player with
                    | ValueSome current, (true, view) ->
                        match current.Publication.Ready with
                        | Some ready -> offer state id observer player current ready view subscribers
                        | None -> ()
                    | _ -> ()

    let private refresh state =
        if state.Options.Enabled then refreshEnabled state
        for KeyValue(id, memberState) in state.Members do
            let required = state.Options.Enabled && memberState.Active && memberState.Located && memberState.Preferences.Publish
                           && state.Audiences.ContainsKey memberState.Player
            let demand = struct (memberState.Context, required)
            if state.Options.Enabled && memberState.Active && memberState.Context <> 0UL && memberState.Demand <> Some demand then
                memberState.Demand <- Some demand
                if not required then memberState.Poses.Latest <- None
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
        FanoutCredit = Credit.create 0L (double options.MaxPoseFanoutPerTick)
        OutgoingPoseCredit = Credit.create 0L (double options.TotalPoseBytesPerSecond)
    }

    let observationMode state =
        if state.Options.Enabled then PhantomObservationMode.Full else PhantomObservationMode.Membership

    let snapshot state = {
        Members = state.Members.Count
        Sources = state.Members.Values |> Seq.filter (fun item -> item.Publication.Ready.IsSome) |> Seq.length
        Subscriptions = state.Members.Values |> Seq.sumBy (fun item -> item.Selected.Count)
        Transfers = state.Transfers.Count
        LatestPoses = state.Members.Values |> Seq.filter (fun item -> item.Poses.Latest.IsSome) |> Seq.length
        PendingIo = state.Transfers.Count
    }

    // Every observer's view of `player` ends; its authority is never reused.
    let private forgetViewsOf state player =
        for observer in state.Members.Values do
            match observer.Views.TryGetValue player with
            | true, view ->
                observer.ClearedAuthority <- max observer.ClearedAuthority view.Authority
                observer.Views.Remove player |> ignore
            | _ -> ()

    let detach state id =
        match state.Members.TryGetValue id with
        | true, memberState ->
            clearSource state memberState
            cancelWhere state Disconnected (fun transfer -> transfer.Owner = id)
            for player in memberState.Selected.Keys do
                unsubscribe state id player
            state.Members.Remove id |> ignore
            state.Players.Remove memberState.Player |> ignore
            forgetViewsOf state memberState.Player
            refresh state
        | _ -> ()

    // A member deactivated by an outbox overflow lost queued Offer/Remove and
    // transfer responses. Policy starts a new phantom session on the client, so
    // retire everything it may not know about while responses are still muted;
    // the following refresh offers the current selection again.
    let private resetSession state id (memberState: Member) =
        memberState.PendingCommands.Clear()
        cancelWhere state Disconnected (fun transfer -> transfer.Owner = id)
        for player in memberState.Selected.Keys do
            unsubscribe state id player
        memberState.Selected.Clear()

    let activate state id =
        match state.Members.TryGetValue id with
        | true, memberState when not memberState.Active ->
            resetSession state id memberState
            memberState.Active <- true
            emit state id (PhantomResponse.Policy(PhantomOptions.policy state.Options))
            refresh state
        | _ -> ()

    let private located (value: PlayerSnapshot) = value.Location.IsSome && value.MovementContext <> 0UL

    let private join (options: PhantomOptions) (value: PlayerSnapshot) : Member = {
        Player = value.Identity.PlayerId
        Character = value.CharacterGeneration
        Context = value.MovementContext
        Located = located value
        Active = false

        Preferences = {
            Publish = options.Enabled
            Receive = options.Enabled
            Maximum = options.Maximum
            Distance = options.Distance
        }
        Demand = None
        Publication = {
            Highest = None
            Bases = struct (None, None)
            Ready = None
            Previous = None
            Settled = None
            CommittedAt = 0L
            NextPublish = 0L
        }
        Poses = {
            Latest = None
            Last = None
            PreviousSequence = 0UL
            Dispatched = None
            AudienceCursor = 0
        }

        Views = Dictionary()
        Selected = Dictionary()
        ClearedAuthority = 0UL
        Revision = 0UL

        Rates = {
            PoseBytes = Credit.create 0L (double options.PoseBytesPerSecond)
            PoseSamples = Credit.create 0L poseBurst
            Replication = Credit.create 0L poseBurst
            OutgoingPoseBytes = Credit.create 0L (double options.PoseBytesPerSecond)
            Commands = Credit.create 0L (double options.CommandsPerSecond)
        }
        PendingCommands = Queue()
        Outbox = Queue()
    }

    // A new character or movement context ends the member's poses and every
    // view to or from it; a new character also ends its published model.
    let private relocate state (memberState: Member) (value: PlayerSnapshot) =
        let publication, poses = memberState.Publication, memberState.Poses
        let characterChanged = memberState.Character <> value.CharacterGeneration
        let contextChanged = memberState.Context <> value.MovementContext || (memberState.Located && value.Location.IsNone)
        if characterChanged || contextChanged then
            trace "context" memberState.Player value.MovementContext 0UL 0UL (if characterChanged then "character" else "movement")
        if characterChanged then
            publication.Bases <- struct (None, None)
            clearSource state memberState
            poses.Last <- None
            poses.Dispatched <- None
        elif contextChanged then
            poses.Latest <- None
            poses.Last <- None
            poses.Dispatched <- None
            publication.Previous <- None
            poses.PreviousSequence <- 0UL
            cancelWhere state ContextChanged (fun transfer -> transfer.Source = memberState.Player)
        memberState.Character <- value.CharacterGeneration
        memberState.Context <- value.MovementContext
        memberState.Located <- located value
        if characterChanged || contextChanged then
            for view in memberState.Views.Values do
                memberState.ClearedAuthority <- max memberState.ClearedAuthority view.Authority
            memberState.Views.Clear()
            forgetViewsOf state memberState.Player
            refresh state

    let rec observe state observation =
        match observation with
        | PhantomObservation.Member(id, value) ->
            match state.Members.TryGetValue id with
            | true, memberState when memberState.Player = value.Identity.PlayerId -> relocate state memberState value
            | false, _ when state.Members.Count < ServerConfig.MaxPeerLimit && not (state.Players.ContainsKey value.Identity.PlayerId) ->
                state.Members[id] <- join state.Options value
                state.Players[value.Identity.PlayerId] <- id
            | _ -> ()
        | PhantomObservation.View(id, sourceConnection, player, revision, distanceSquared, facing) ->
            let mutable observer = Unchecked.defaultof<Member>
            let mutable currentSource = Guid.Empty
            // Presence captures the source session epoch when it projects a view.
            // A queued fact cannot bind an absent or reconnected PlayerId.
            if state.Members.TryGetValue(id, &observer) && observer.Player <> player
               && state.Players.TryGetValue(player, &currentSource) && currentSource = sourceConnection
               && Double.IsFinite distanceSquared && distanceSquared >= 0.0 then
                let mutable view = Unchecked.defaultof<View>
                if observer.Views.TryGetValue(player, &view) then
                    if revision >= view.Authority && (revision <> view.Authority || distanceSquared <> view.DistanceSquared || facing <> view.Facing) then
                        observer.Views[player] <- {
                            Authority = revision
                            DistanceSquared = distanceSquared
                            Facing = facing
                        }
                elif revision > observer.ClearedAuthority then
                    observer.Views[player] <- {
                        Authority = revision
                        DistanceSquared = distanceSquared
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

    let private refuse state owner player generation upload request retry outcome reason =
        let completion = {
            Target = {
                Player = player
                Generation = generation
            }
            Upload = upload
            RetryAfterMs = retry
        }
        emit state owner (PhantomResponse.Complete(PhantomTransferId 0UL, outcome, reason, request, Some completion))
        deny()

    let private startTransfer state at owner player (manifest: PhantomManifest) character context revision upload request change basis =
        if not (transferAvailable state owner) then
            refuse state owner player manifest.Generation upload request (transferRetryMs state.Options upload) PhantomOutcome.TransferLimit "transfer limit"
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
                    && (selected.DisplayProgressBytes > 0 || (state.Transfers.Values |> Seq.exists (downloadOf id player)))
                | _ -> false)
        | _ -> false

    // Members with a committed model or a first upload in flight.
    let private sourceCount state =
        let sources = state.Members.Values |> Seq.filter (fun item -> item.Publication.Ready.IsSome) |> Seq.length
        let pending = state.Transfers.Values |> Seq.filter (fun item ->
            item.Upload && (match source state item.Source with ValueSome current -> current.Publication.Ready.IsNone | ValueNone -> false)) |> Seq.length
        sources + pending

    // The first rule a publish breaks, with its retry delay and log text; None admits it.
    let private publishRefusal state at id (memberState: Member) (manifest: PhantomManifest) context (change: PhantomDelta option) =
        let publication = memberState.Publication
        let struct (basis, priorBasis) = publication.Bases
        let stale = publication.Highest |> Option.exists (fun highest ->
            manifest.Generation.Value < highest.Generation.Value || (manifest.Generation = highest.Generation && manifest <> highest))
        let replacing = publication.Ready |> Option.exists (fun ready -> manifest <> ready)
        let existingSource = publication.Ready.IsSome || (state.Transfers.Values |> Seq.exists (fun item -> item.Upload && item.Owner = id))
        if change |> Option.exists (fun d -> basis <> Some d.BaseHash && priorBasis <> Some d.BaseHash) then
            Some(noRetry, PhantomOutcome.DeltaBaseUnavailable, "delta base unavailable")
        elif not memberState.Preferences.Publish || not memberState.Located || context <> memberState.Context || stale then
            Some(noRetry, PhantomOutcome.PublishRefused, "publish admission")
        elif publication.Ready |> Option.exists (fun ready -> publication.Settled <> Some ready.Generation && manifest <> ready) then
            Some(retryDelayMs, PhantomOutcome.ReplacementPending, "replacement pending")
        elif replacing && awaitingFirstDisplay state at memberState.Player then
            Some(retryDelayMs, PhantomOutcome.InitialDisplayPending, "initial display pending")
        elif at < publication.NextPublish then
            Some(int (publication.NextPublish - at), PhantomOutcome.PublishCooldown, "publish cooldown")
        elif not existingSource && sourceCount state >= state.Options.MaxSources then
            Some(publishRetryMs state.Options, PhantomOutcome.SourceLimit, "source limit")
        elif not (transferAvailable state id) then
            Some(publishRetryMs state.Options, PhantomOutcome.TransferLimit, "transfer limit")
        else None

    // Pending publication owns no visible scene. Supersede only uploads;
    // current poses and downloads remain usable meanwhile.
    let private beginPublish state at id (memberState: Member) (manifest: PhantomManifest) context request change =
        let publication, poses = memberState.Publication, memberState.Poses
        cancelWhere state Superseded (fun transfer -> transfer.Upload && transfer.Owner = id)
        publication.Previous <- publication.Ready |> Option.map _.Generation
        match poses.Last with
        | Some struct (generation, sequence) when publication.Previous = Some generation -> poses.PreviousSequence <- sequence
        | _ -> ()
        publication.Highest <- Some manifest
        publication.NextPublish <- at + int64 state.Options.PublishCooldownMs
        startTransfer state at id memberState.Player manifest memberState.Character context 0UL true request change None
        refresh state

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
                match publishRefusal state at id memberState manifest context change with
                | Some(retry, outcome, reason) -> refuse state id memberState.Player manifest.Generation true request retry outcome reason
                | None -> beginPublish state at id memberState manifest context request change
            | PhantomRequest.Displayed(player, revision, generation) ->
                match memberState.Selected.TryGetValue player with
                | true, selected when selected.Revision = revision && selected.Asset.Generation = generation ->
                    traceFor (uint64 memberState.Player) "displayed" player memberState.Context generation.Value revision ""
                    selected.Displayed <- Some generation
                | _ -> deny()
            | PhantomRequest.Download(player, generation, request, basis) ->
                match memberState.Selected.TryGetValue player, source state player with
                | (true, selected), ValueSome current when selected.Asset.Generation = generation ->
                    // A repeated request for the download in flight changes nothing.
                    let previous = state.Transfers.Values |> Seq.filter (downloadOf id player) |> Seq.toArray
                    if previous |> Array.forall (fun item -> item.Request <> request) then
                        for transfer in previous do cancel state transfer.Id Superseded
                        startTransfer state at id player selected.Asset current.Character current.Context selected.Revision false request None basis
                | _ -> refuse state id player generation false request retryDelayMs PhantomOutcome.ViewUnavailable "view unavailable"
            | PhantomRequest.Cancel transferId ->
                match state.Transfers.TryGetValue transferId with
                | true, transfer when transfer.Owner = id -> cancel state transferId Cancelled
                | _ -> deny()
        | _ -> deny()

    let private takeCommand at (rate: int) credit =
        Credit.refill at (min commandBurst (double rate)) (double rate) credit
        Credit.trySpend 1.0 credit

    let private commandReady state at (memberState: Member) =
        memberState.Outbox.Count < commandResponseBacklog && takeCommand at state.Options.CommandsPerSecond memberState.Rates.Commands

    // Reliable requests are deferred, not dropped: a client waits up to its
    // request timeout for a lost Download/Publish and a source cannot settle
    // without Displayed. One second of the configured command rate bounds the
    // wait; only a peer outside the request/response contract exceeds it.
    let private admitCommand state at id (memberState: Member) request =
        if memberState.PendingCommands.Count = 0 && commandReady state at memberState then handle state at id request
        elif memberState.PendingCommands.Count < state.Options.CommandsPerSecond then memberState.PendingCommands.Enqueue request
        else deny()

    let private drainCommands state at id (memberState: Member) =
        while memberState.Active && memberState.PendingCommands.Count > 0 && commandReady state at memberState do
            handle state at id (memberState.PendingCommands.Dequeue())

    let private publishes (asset: PhantomManifest option) generation =
        match asset with
        | Some asset -> asset.Generation = generation
        | None -> false

    // A pose joins the source's stream only for a generation it publishes and
    // only ahead of that generation's sequence floor.
    let private admitPose at (memberState: Member) (pose: PhantomPose) =
        let publication, poses = memberState.Publication, memberState.Poses
        let pending = if publication.Previous.IsSome then publication.Highest else None
        let known = publishes publication.Ready pose.Generation || publishes pending pose.Generation
        // A failed pending upload may fall back to its still-ready model.
        // Keep both sequence floors: accepting that fallback must not reopen
        // the pending generation to delayed/reordered packets.
        let fallback = publication.Previous = Some pose.Generation && publishes publication.Ready pose.Generation
        let advancing =
            match poses.Last with
            | Some struct (generation, sequence) when generation = pose.Generation -> pose.Sequence.Value > sequence
            | Some struct (generation, _) -> pose.Generation.Value > generation.Value || (fallback && pose.Sequence.Value > poses.PreviousSequence)
            | None -> true
        let validPrevious = pose.Previous |> Option.forall (fun previous ->
            publication.Previous = Some previous.Generation || publication.Settled = Some pose.Generation)
        if known && advancing && validPrevious then
            let accepted = if publication.Previous.IsNone then PhantomPose.withoutPrevious pose else pose
            if fallback then poses.PreviousSequence <- pose.Sequence.Value
            match poses.Last with
            | Some struct (generation, _) when generation.Value > pose.Generation.Value -> ()
            | _ -> poses.Last <- Some(struct (pose.Generation, pose.Sequence.Value))
            pose.Previous |> Option.iter (fun previous -> poses.PreviousSequence <- max poses.PreviousSequence previous.Sequence.Value)
            poses.Latest <- Some(struct (accepted, at))
            true
        else false

    /// Large poses are admitted before protobuf allocation. Model data uses the
    /// admitted transfer window; control requests have a separate burst budget.
    let receive state at id lane (bytes: byte array) =
        match state.Members.TryGetValue id with
        | true, memberState when not memberState.Active && memberState.Outbox.Count = 0 -> activate state id
        | _ -> ()
        match state.Members.TryGetValue id with
        | true, memberState when memberState.Active && state.Options.Enabled ->
            match lane with
            | DeliveryLane.Poses ->
                let rates = memberState.Rates
                Credit.refill at poseBurst (1000.0 / double state.Options.PoseIntervalMs) rates.PoseSamples
                if rates.PoseSamples.Available >= 1.0 && Credit.tryTake at state.Options.PoseBytesPerSecond bytes.Length rates.PoseBytes then
                    Credit.spend 1.0 rates.PoseSamples
                    match PhantomCodec.decodePose state.Options bytes with
                    | Ok pose when memberState.Located && memberState.Preferences.Publish && pose.Context = memberState.Context ->
                        if not (admitPose at memberState pose) then deny()
                    | _ -> deny()
                else deny()
            | DeliveryLane.Models ->
                match PhantomCodec.decodeAsset state.Options bytes with
                | Ok request -> admitCommand state at id memberState request
                | Error _ -> deny()
            | DeliveryLane.Control | DeliveryLane.Chat | DeliveryLane.Realtime -> deny()
        | _ -> deny()

    let private flushOutbox state id (memberState: Member) =
        let mutable sending = true
        let mutable count = 0
        while sending && memberState.Outbox.Count > 0 && count < responsesPerFlush do
            match state.Send(id, memberState.Outbox.Peek()) with
            | Ok () ->
                memberState.Outbox.Dequeue() |> ignore
                count <- count + 1
            | Error _ -> sending <- false

    let private settle state at (transfer: Transfer) =
        if not (valid state transfer) then cancel state transfer.Id StaleTransfer
        elif at - transfer.Touched > int64 state.Options.TransferTimeoutMs then cancel state transfer.Id TransferTimeout
        else
            let commit () =
                let owner = state.Members[transfer.Owner]
                let publication = owner.Publication
                if publication.Ready <> Some transfer.Manifest then
                    // A pose for the pending generation can already be admitted.
                    // Commit must not reopen its sequence floor or discard it.
                    owner.Poses.Latest <- owner.Poses.Latest |> Option.filter (fun struct (pose, _) -> pose.Generation = transfer.Manifest.Generation)
                trace "committed" owner.Player owner.Context transfer.Manifest.Generation.Value 0UL ""
                let struct (basis, _) = publication.Bases
                if basis <> Some transfer.Manifest.Hash then publication.Bases <- struct (Some transfer.Manifest.Hash, basis)
                publication.Ready <- Some transfer.Manifest
                publication.CommittedAt <- at
                cancel state transfer.Id Accepted
            let beginHttp () =
                let lease = state.Http.Admit(transfer.Owner, transfer.Id, transfer.Manifest, transfer.Upload, transfer.Delta)
                transfer.Phase <- StreamingHttp lease
                emit state transfer.Owner (PhantomResponse.Transfer(transfer.Id, transfer.Manifest, transfer.Source, transfer.Upload, transfer.Request, lease.Token, transfer.Delta))
            match transfer.Phase with
            | StartingUpload pending when pending.IsCompletedSuccessfully ->
                match pending.Result with
                | Error reason -> cancel state transfer.Id (StorageFailure reason)
                | Ok true -> commit()
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
                    | Ok () when transfer.Upload -> commit()
                    | Ok () -> cancel state transfer.Id Accepted
            | StartingUpload _ | StartingDownload _ -> ()

    // Whether an observer of `player` still loads `generation` within the progress timeout.
    let private awaitingDisplay state at player generation =
        match state.Audiences.TryGetValue player with
        | true, audience ->
            audience |> Seq.exists (fun peer ->
                match state.Members[peer].Selected.TryGetValue player with
                | true, selected ->
                    selected.Displayed <> Some generation && at - selected.DisplayProgressAt < int64 state.Options.TransferTimeoutMs
                | _ -> false)
        | _ -> false

    // A replacement settles once no observer still waits to display it; the
    // bridge from the previous generation ends with it.
    let private settleSources state at =
        for KeyValue(id, owner) in state.Members do
            let publication = owner.Publication
            match publication.Ready with
            | Some ready when publication.Settled <> Some ready.Generation && publication.Highest = Some ready ->
                if not (awaitingDisplay state at owner.Player ready.Generation) then
                    publication.Settled <- Some ready.Generation
                    publication.Previous <- None
                    owner.Poses.PreviousSequence <- 0UL
                    owner.Poses.Latest <- owner.Poses.Latest |> Option.map (fun struct (pose, received) -> struct (PhantomPose.withoutPrevious pose, received))
                    emit state id (PhantomResponse.Settled(ready.Generation, owner.Context))
            | _ -> ()

    // Reliable control/ACKs follow IO completion, not the pose cadence.
    // At 10 Hz, gating a 64 KiB window here limited uploads to 640 KiB/s.
    // Deferred commands follow once their backlog has drained; their responses
    // leave on the next turn.
    let private serviceCommands state at =
        for KeyValue(id, memberState) in state.Members do
            flushOutbox state id memberState
            drainCommands state at id memberState

    // Sends one source's latest pose to the observers next in its audience
    // rotation; returns the inspection budget left for this turn.
    let private fanoutSource state at period (current: Member) budget =
        let poses, rates = current.Poses, current.Rates
        let struct (pose, receivedAt) = poses.Latest.Value
        let identity = struct (pose.Generation, pose.Sequence.Value)
        let admitted = poses.Dispatched = Some identity
        Credit.refill at poseBurst (1000.0 / double period) rates.Replication
        if at - receivedAt > int64 state.Options.PoseTimeoutMs then
            poses.Latest <- None
            budget
        elif not admitted && rates.Replication.Available < 1.0 then budget
        else
            let mutable budget = budget
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
                let id = audience[(poses.AudienceCursor + visited) % audience.Length]
                visited <- visited + 1
                budget <- budget - 1
                let observer = state.Members[id]
                match observer.Selected.TryGetValue current.Player with
                | true, selected when observer.Active && (selected.SentGeneration <> Some pose.Generation || pose.Sequence.Value > selected.SentSequence) ->
                    let length = PhantomCodec.posePacketSize current.Player selected.Revision pose
                    Credit.refillSecond at state.Options.PoseBytesPerSecond observer.Rates.OutgoingPoseBytes
                    Credit.refillSecond at state.Options.TotalPoseBytesPerSecond state.OutgoingPoseCredit
                    if observer.Outbox.Count > 0 then ()
                    elif state.OutgoingPoseCredit.Available < double length then
                        // End this turn at exhaustion. Scanning every remaining
                        // source would wrap the cursor back to the same prefix,
                        // letting it consume every refill and starve the tail.
                        budget <- 0
                    elif observer.Rates.OutgoingPoseBytes.Available >= double length then
                        let packet =
                            match encoded.TryGetValue selected.Revision with
                            | true, packet -> packet
                            | false, _ ->
                                let packet = PhantomCodec.encodePose current.Player selected.Revision pose
                                encoded[selected.Revision] <- packet
                                packet
                        match state.Send(id, packet) with
                        | Ok () ->
                            Credit.spend (double length) observer.Rates.OutgoingPoseBytes
                            Credit.spend (double length) state.OutgoingPoseCredit
                            Credit.spend 1.0 state.FanoutCredit
                            sent <- true
                            selected.SentSequence <- pose.Sequence.Value
                            selected.SentGeneration <- Some pose.Generation
                            fanout.Add 1L
                        | Error _ -> ()
                | _ -> ()
            poses.AudienceCursor <- (poses.AudienceCursor + visited) % max 1 audience.Length
            if sent && not admitted then
                Credit.spend 1.0 rates.Replication
                poses.Dispatched <- Some identity
            budget

    // Small turns avoid resampling all sources on one 100 ms grid.
    // Source credit preserves the configured rate with a two-snapshot burst;
    // Shared send credit preserves aggregate throughput; each turn still has a
    // bounded inspection budget, including subscriptions already up to date.
    let private dispatchPoses state at period =
        let dispatchPeriod = max 1L (min maxDispatchPeriodMs (period / 4L))
        if at - state.LastDispatch >= dispatchPeriod then
            state.LastDispatch <- at - (at - state.LastDispatch) % dispatchPeriod
            let fanoutLimit = double state.Options.MaxPoseFanoutPerTick
            Credit.refill at fanoutLimit (fanoutLimit * 1000.0 / double period) state.FanoutCredit
            let sources =
                if state.Options.Enabled then state.Members.Values |> Seq.filter (fun item -> item.Active && item.Poses.Latest.IsSome) |> Seq.toArray
                else [||]
            let mutable budget = state.Options.MaxPoseFanoutPerTick
            let mutable visitedSources = 0
            while visitedSources < sources.Length && budget > 0 && state.FanoutCredit.Available >= 1.0 do
                let current = sources[(state.Cursor + visitedSources) % sources.Length]
                visitedSources <- visitedSources + 1
                budget <- fanoutSource state at period current budget
            state.Cursor <- (state.Cursor + visitedSources) % ServerConfig.MaxPeerLimit

    let private tickActive state at =
        // Cleanup completions are bounded by admitted transfers, and drained even
        // when publishing is disabled. No file work runs inside this turn.
        state.Cleanup.RemoveAll(Predicate(completedCleanup)) |> ignore
        for transfer in state.Transfers.Values |> Seq.toArray do
            settle state at transfer
        let period = int64 state.Options.ReplicationIntervalMs
        if at - state.LastTick >= period then
            // Keep the cadence grid across late runtime ticks; one latest pose,
            // never a burst of duplicate catch-up snapshots.
            state.LastTick <- at - (at - state.LastTick) % period
            refresh state
            settleSources state at
        serviceCommands state at
        dispatchPoses state at period

    let tick state at =
        if (failure state).IsNone then tickActive state at

    let stop state =
        for id in state.Members.Keys |> Seq.toArray do detach state id
