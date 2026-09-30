namespace Dreamsleeve.Server.Core

open System
open System.Collections.Generic
open Microsoft.Extensions.Logging
open Dreamsleeve.Agent
open Dreamsleeve.Server.Domain

/// Owns every mark, the spatial index over them, the author profiles for the
/// wire and each observer's visible set. Marks are persistent: every change
/// goes to the bounded writer; memory is authoritative while the server runs.
[<RequireQualifiedAccess>]
module GroundMarksAgent =
    type private Observer = {
        ConnectionId: Guid
        Profile: PlayerData
        Events: ReliableAgentRef<GroundMarkEvent>
        mutable Location: PlayerLocation voption
        mutable Cell: SpatialIndex.Cell voption
        mutable Generation: uint64
        Visible: HashSet<GroundMarkId>
        mutable Revision: uint64
    }

    type private State = {
        Options: GroundMarkOptions
        Rules: GroundMarkRules
        Marks: GroundMarkStorage
        /// Current moderated profile per author with at least one mark, for the
        /// wire; never a pseudonym: those stay with the marks placed under them.
        Authors: Dictionary<PlayerId, PlayerData>
        Index: SpatialIndex.State<GroundMarkId>
        Observers: Dictionary<Guid, Observer>
        Players: Dictionary<PlayerId, Guid>
        Notes: RateLimit.State
        /// Last accepted death report per account, Environment.TickCount64.
        Deaths: Dictionary<PlayerId, int64>
        Candidates: HashSet<GroundMarkId>
        Writer: AgentOutbox<GroundMarkWrite>
        Host: AgentOutbox<SessionHostCommand>
        Logger: ILogger
        mutable NextId: uint64
        mutable Ticker: AgentTicker option
    }

    let private notifyHost state context command =
        if not (state.Host.TrySend(context, command)) then context.Abort()

    // Storage is the source of truth between runs: a write that cannot even be
    // queued means persistence is broken, and the owner stops visibly.
    let private persist state (context: AgentContext<GroundMarkCommand>) write =
        if not (state.Writer.TrySend(context, write)) then
            state.Logger.LogError("Ground mark persistence queue is full; stopping the owner")
            context.Abort()

    let private remove state connectionId =
        match state.Observers.TryGetValue connectionId with
        | false, _ -> ()
        | true, observer ->
            state.Observers.Remove connectionId |> ignore
            match state.Players.TryGetValue observer.Profile.PlayerId with
            | true, owner when owner = connectionId -> state.Players.Remove observer.Profile.PlayerId |> ignore
            | true, _ | false, _ -> ()

    let private deliver state context (observer: Observer) event =
        match observer.Events.TryPost event with
        | AgentTryDeliveryResult.Posted -> ()
        | AgentTryDeliveryResult.Closed -> remove state observer.ConnectionId
        | AgentTryDeliveryResult.Full ->
            remove state observer.ConnectionId
            notifyHost state context (SessionHostCommand.SlowConsumer observer.ConnectionId)

    let private record state (mark: GroundMark) : GroundMarkRecord =
        { Mark = mark; Author = GroundMark.authorIdentity state.Authors[mark.Author] mark }

    /// The author's complete set, wherever the marks stand; sent whenever it changes.
    let private announceOwn state context author =
        match state.Players.TryGetValue author with
        | false, _ -> ()
        | true, connectionId ->
            match state.Observers.TryGetValue connectionId with
            | false, _ -> ()
            | true, observer ->
                let own = GroundMarkStorage.ofAuthor author state.Marks |> List.map (record state)
                deliver state context observer (GroundMarkEvent.Own own)

    let private nextRevision (observer: Observer) =
        observer.Revision <- observer.Revision + 1UL
        observer.Revision

    let private changed state context (observer: Observer) added removed clear =
        if clear || not (List.isEmpty added) || not (List.isEmpty removed) then
            let view = { ViewRevision = nextRevision observer; Added = added |> List.map (record state); Removed = removed; Clear = clear }
            deliver state context observer (GroundMarkEvent.Changed view)

    /// Recomputes what this observer sees from the index. A new baseline (clear)
    /// resends every visible mark; otherwise only the difference travels. An
    /// observer that saw nothing and still sees nothing hears nothing.
    let private refresh state context (observer: Observer) clear =
        let hadAny = observer.Visible.Count > 0
        match observer.Location with
        | ValueNone ->
            observer.Visible.Clear()
            if hadAny then changed state context observer [] [] true
        | ValueSome location ->
            let candidates = state.Candidates
            candidates.Clear()
            observer.Cell |> ValueOption.iter (fun cell -> SpatialIndex.neighborsOf cell state.Index candidates)
            let visible =
                candidates
                |> Seq.choose (fun id ->
                    match GroundMarkStorage.tryFind id state.Marks with
                    | ValueSome mark when GroundMark.isVisibleFrom state.Rules.VisibilityDistance (ValueSome location) mark -> Some mark
                    | ValueSome _ | ValueNone -> None)
                |> Seq.sortBy _.Id
                |> List.ofSeq
            candidates.Clear()
            let added = if clear then visible else visible |> List.filter (fun mark -> not (observer.Visible.Contains mark.Id))
            let removed =
                if clear then []
                else
                    let now = HashSet(visible |> Seq.map _.Id)
                    observer.Visible |> Seq.filter (fun id -> not (now.Contains id)) |> Seq.sort |> List.ofSeq
            observer.Visible.Clear()
            for mark in visible do observer.Visible.Add mark.Id |> ignore
            changed state context observer added removed (clear && (hadAny || not visible.IsEmpty))

    let private join state context (subscription: Subscription<GroundMarkEvent>) =
        // The runtime frees an account only after this owner acknowledged its
        // detach, so another connection of the same account is a broken invariant.
        match state.Players.TryGetValue subscription.Profile.PlayerId with
        | true, previous when previous <> subscription.ConnectionId ->
            notifyHost state context (SessionHostCommand.Close(subscription.ConnectionId, "ground_marks_identity_conflict"))
        | true, _ | false, _ ->
            let observer = {
                ConnectionId = subscription.ConnectionId; Profile = subscription.Profile; Events = subscription.Events
                Location = ValueNone; Cell = ValueNone; Generation = 0UL; Visible = HashSet(); Revision = 0UL
            }
            state.Observers[subscription.ConnectionId] <- observer
            state.Players[subscription.Profile.PlayerId] <- subscription.ConnectionId
            if state.Authors.ContainsKey subscription.Profile.PlayerId then
                state.Authors[subscription.Profile.PlayerId] <- subscription.Profile
            // The player learns every own mark at once, even those far from here.
            announceOwn state context subscription.Profile.PlayerId

    let private observe state context connectionId generation (location: PlayerLocation voption) =
        match state.Observers.TryGetValue connectionId with
        | false, _ -> ()
        | true, observer ->
            let cell = location |> ValueOption.map (fun value -> SpatialIndex.cellOf state.Index value.Location.LocationId value.Position)
            let spaceChanged =
                match observer.Cell, cell with
                | ValueSome previous, ValueSome next -> previous.Space <> next.Space
                | ValueNone, ValueSome _ -> true
                | ValueSome _, ValueNone | ValueNone, ValueNone -> false
            let generationChanged = observer.Generation <> generation
            let cellChanged = observer.Cell <> cell
            observer.Location <- location
            observer.Cell <- cell
            observer.Generation <- generation
            if generationChanged || cellChanged then
                refresh state context observer (generationChanged || spaceChanged)

    let private reject state context (observer: Observer) requestId code message field =
        deliver state context observer (GroundMarkEvent.Rejected(requestId, { Code = code; Message = message; Field = field }))

    /// Frequency per stable account: notes share a token bucket with a
    /// repeated-text memory, deaths keep a minimum interval.
    let private admit state (observer: Observer) (submission: GroundMarkSubmission) =
        let now = Environment.TickCount64
        let playerId = observer.Profile.PlayerId
        match submission.Body with
        | GroundMarkBody.Note _ ->
            match RateLimit.admit state.Notes now playerId submission.Fingerprint with
            | Ok () -> Ok ()
            | Error RateLimit.Refusal.Repeated -> Error "The same note was left too recently."
            | Error RateLimit.Refusal.Exhausted -> Error "Too many notes. Wait a moment."
        | GroundMarkBody.Death _ ->
            match state.Deaths.TryGetValue playerId with
            | true, last when now - last < int64 state.Options.DeathMinIntervalMs -> Error "Deaths are reported too often."
            | true, _ | false, _ ->
                state.Deaths[playerId] <- now
                Ok ()

    let private forget state (mark: GroundMark) =
        GroundMarkStorage.remove mark.Id state.Marks |> ignore
        SpatialIndex.remove mark.Id state.Index
        if not (GroundMarkStorage.authorHasMarks mark.Author state.Marks) then
            state.Authors.Remove mark.Author |> ignore

    /// Tells every observer that had these marks; one delta per observer.
    let private announceRemoved state context (ids: GroundMarkId list) =
        let observers = state.Observers.Values |> Seq.toArray
        for observer in observers do
            if state.Observers.ContainsKey observer.ConnectionId then
                let gone = ids |> List.filter observer.Visible.Remove
                changed state context observer [] gone false

    let private place state context (submission: GroundMarkSubmission) =
        match state.Observers.TryGetValue submission.ConnectionId with
        | false, _ -> ()
        | true, observer ->
            let author = observer.Profile.PlayerId
            let kind = GroundMarkBody.kind submission.Body
            let cell = SpatialIndex.cellOf state.Index submission.Placement.LocationId submission.Placement.Position
            let candidate = GroundMarkStorage.evictionCandidate state.Rules author kind state.Marks
            let freed =
                match candidate with
                | ValueSome oldest when SpatialIndex.cellOf state.Index oldest.Placement.LocationId oldest.Placement.Position = cell -> 1
                | ValueSome _ | ValueNone -> 0
            match admit state observer submission with
            | Error message -> reject state context observer submission.RequestId RequestRejectionCode.RateLimited message "text"
            | Ok () when SpatialIndex.countAt cell state.Index - freed >= state.Options.MaxPerIndexCell ->
                reject state context observer submission.RequestId RequestRejectionCode.GroundMarkAreaFull
                    "This area already holds as many marks as it can." "placement"
            | Ok () ->
                match GroundMarkId.create state.NextId with
                | Error _ -> context.Abort()
                | Ok id ->
                    let now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                    let mark =
                        GroundMark.create id author submission.Body submission.Placement now
                        |> GroundMark.withFlagged submission.Flagged
                        |> GroundMark.withCharacterName submission.CharacterName
                        |> GroundMark.withPseudonym submission.Pseudonym
                        |> GroundMark.withGameDate (ValueSome submission.GameDate)
                    match GroundMarkStorage.add state.Rules mark state.Marks with
                    | Error _ -> context.Abort()
                    | Ok evicted ->
                        state.NextId <- if state.NextId = UInt64.MaxValue then 0UL else state.NextId + 1UL
                        state.Authors[author] <- observer.Profile
                        evicted |> ValueOption.iter (fun old ->
                            SpatialIndex.remove old.Id state.Index
                            persist state context (GroundMarkWrite.Delete [old.Id]))
                        SpatialIndex.setCell mark.Id (ValueSome cell) state.Index
                        persist state context (GroundMarkWrite.Insert mark)
                        let evictedId = evicted |> ValueOption.map _.Id
                        deliver state context observer (GroundMarkEvent.Placed(submission.RequestId, record state mark, evictedId))
                        state.Logger.LogInformation("Ground mark {MarkId} ({Kind}) placed by player {PlayerId} in {Location}{Evicted}",
                                                    GroundMarkId.value mark.Id, mark.Kind, PlayerId.value author,
                                                    PluginName.value mark.Placement.LocationId.PluginName + ":" + (LocalFormId.value mark.Placement.LocationId.LocalFormId).ToString("X6"),
                                                    (match evictedId with ValueSome id -> $", evicting {GroundMarkId.value id}" | ValueNone -> ""))
                        announceOwn state context author
                        // The author learns about the visible set through the same delta as everyone.
                        let observers = state.Observers.Values |> Seq.toArray
                        for recipient in observers do
                            if state.Observers.ContainsKey recipient.ConnectionId then
                                let gone = evictedId |> ValueOption.filter recipient.Visible.Remove |> ValueOption.toList
                                let added =
                                    if GroundMark.isVisibleFrom state.Rules.VisibilityDistance recipient.Location mark then
                                        recipient.Visible.Add mark.Id |> ignore
                                        [mark]
                                    else []
                                changed state context recipient added gone false

    let private removeMark state context connectionId requestId id =
        match state.Observers.TryGetValue connectionId with
        | false, _ -> ()
        | true, observer ->
            match GroundMarkStorage.tryFind id state.Marks with
            | ValueSome mark when mark.Author = observer.Profile.PlayerId ->
                forget state mark
                persist state context (GroundMarkWrite.Delete [id])
                deliver state context observer (GroundMarkEvent.Removed(requestId, id))
                state.Logger.LogInformation("Ground mark {MarkId} removed by its author, player {PlayerId}", GroundMarkId.value id, PlayerId.value mark.Author)
                announceOwn state context mark.Author
                announceRemoved state context [id]
            | ValueSome _ | ValueNone ->
                reject state context observer requestId RequestRejectionCode.GroundMarkNotFound "No such mark of yours." "mark_id"

    let private expire state context =
        let now = DateTimeOffset.UtcNow
        let expired = GroundMarkStorage.expired state.Rules now state.Marks
        if not expired.IsEmpty then
            for mark in expired do forget state mark
            let ids = expired |> List.map _.Id
            persist state context (GroundMarkWrite.Delete ids)
            state.Logger.LogInformation("Removed {Count} expired ground marks", ids.Length)
            announceRemoved state context ids
            for author in expired |> List.map _.Author |> List.distinct do
                announceOwn state context author

    let private detach state (context: AgentContext<GroundMarkCommand>) (request: SessionDetach) =
        remove state request.ConnectionId
        match request.ReplyTo.TryPost request.ConnectionId with
        | AgentTryDeliveryResult.Posted | AgentTryDeliveryResult.Closed -> ()
        | AgentTryDeliveryResult.Full -> context.Abort()

    let private schedule state context =
        if state.Ticker.IsNone then
            state.Ticker <- Some (AgentTicker.start (TimeSpan.FromMilliseconds(int64 state.Options.ExpiryCheckIntervalMs)) context GroundMarkCommand.Expire)

    let private handle state (context: AgentContext<GroundMarkCommand>) command = task {
        schedule state context
        match command with
        | GroundMarkCommand.Join subscription -> join state context subscription
        | GroundMarkCommand.Observe(connectionId, generation, location) -> observe state context connectionId generation location
        | GroundMarkCommand.Place submission -> place state context submission
        | GroundMarkCommand.Remove(connectionId, requestId, id) -> removeMark state context connectionId requestId id
        | GroundMarkCommand.Expire _ ->
            expire state context
            state.Ticker |> Option.iter _.Acknowledge()
        | GroundMarkCommand.Detach request -> detach state context request
        | GroundMarkCommand.Rename(connectionId, profile) ->
            match state.Observers.TryGetValue connectionId with
            | true, observer when observer.Profile.PlayerId = profile.PlayerId ->
                state.Observers[connectionId] <- { observer with Profile = profile }
                if state.Authors.ContainsKey profile.PlayerId then state.Authors[profile.PlayerId] <- profile
            | true, _ | false, _ -> ()
    }

    let private isControl = function
        | GroundMarkCommand.Join _ | GroundMarkCommand.Expire _ | GroundMarkCommand.Detach _ -> true
        | GroundMarkCommand.Observe _ | GroundMarkCommand.Place _ | GroundMarkCommand.Remove _ | GroundMarkCommand.Rename _ -> false

    /// loaded are the stored marks with their authors' current profiles; nextId
    /// is the storage high-water mark plus one, so IDs never repeat across runs.
    /// Expired marks among them are removed at the first expiry pass.
    /// The options and their rules come checked by GameSettings.create.
    let start (options: GroundMarkOptions) (rules: GroundMarkRules) (loaded: StoredGroundMark list) (nextId: uint64)
              (writer: ReliableAgentRef<GroundMarkWrite>) (host: ReliableAgentRef<SessionHostCommand>) (logger: ILogger) =
        let state = {
            Options = options; Rules = rules
            Marks = GroundMarkStorage.create (); Authors = Dictionary()
            Index = SpatialIndex.create (double options.VisibilityDistance)
            Observers = Dictionary(); Players = Dictionary()
            Notes = RateLimit.create options.NoteRate
            Deaths = Dictionary(); Candidates = HashSet()
            Writer = AgentOutbox(options.MaxPendingWrites, writer)
            Host = AgentOutbox(options.MaxControlDeliveries, host)
            Logger = logger
            NextId = max nextId 1UL
            Ticker = None
        }
        let mutable highest = 0UL
        let duplicates = ResizeArray<StoredGroundMark>()
        for entry in loaded |> List.sortBy (fun entry -> entry.Mark.Id) do
            match GroundMarkStorage.add rules entry.Mark state.Marks with
            | Ok _ ->
                state.Authors[entry.Mark.Author] <- entry.Author
                SpatialIndex.setCell entry.Mark.Id
                    (ValueSome (SpatialIndex.cellOf state.Index entry.Mark.Placement.LocationId entry.Mark.Placement.Position)) state.Index
                highest <- max highest (GroundMarkId.value entry.Mark.Id)
            | Error _ -> duplicates.Add entry
        if duplicates.Count > 0 then Error (sprintf "Stored ground marks contain %d duplicate IDs." duplicates.Count)
        elif highest >= state.NextId then Error (sprintf "Stored ground mark ID %d is not below the next ID %d." highest state.NextId)
        else
            let settings = {
                AgentOptions.create "ground-marks" with
                    Mailbox = AgentMailbox.boundedWithControl options.MailboxCapacity options.ControlReserve
            }
            let agent = Agent.Start(settings, handle state, isControl = isControl)
            let now = System.Diagnostics.Stopwatch.GetTimestamp()
            agent.TryPost(GroundMarkCommand.Expire { DueTimestamp = now; QueuedTimestamp = now }) |> ignore
            Ok agent
