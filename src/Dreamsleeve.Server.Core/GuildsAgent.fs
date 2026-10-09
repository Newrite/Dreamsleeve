namespace Dreamsleeve.Server.Core

open System
open System.Collections.Generic
open Microsoft.Extensions.Logging
open Dreamsleeve.Agent
open Dreamsleeve.Server.Domain

/// Owns every guild (docs/GuildsRu.md): members, roles, guild mutes and
/// invitations, each guild's chat history and the sessions of members online.
/// Guilds are persistent: every change goes to the bounded writer in order and
/// memory is authoritative while the server runs; guild chat lives in memory.
[<RequireQualifiedAccess>]
module GuildsAgent =
    type private State = {
        Options: GuildOptions
        Book: GuildBook
        Moderation: ModerationRules
        /// Moderated profile of every member and invited player, for the wire
        /// and the panel; guildmates see these real names.
        Profiles: Dictionary<PlayerId, PlayerData>
        Chats: Dictionary<GuildId, Chat>
        NextMessageIds: Dictionary<GuildId, uint64>
        Subscribers: Dictionary<Guid, Subscription<GuildEvent>>
        Online: Dictionary<PlayerId, Guid>
        /// Sessions a delivery failed for; they hear nothing until they detach.
        Failed: HashSet<Guid>
        /// Guild chat admission per account, like the global channel's.
        Senders: RateLimit.State
        Writer: AgentOutbox<GuildWrite>
        Host: AgentOutbox<SessionHostCommand>
        Logger: ILogger
        mutable NextId: uint64
        mutable Ticker: AgentTicker option
    }

    let private notifyHost state context command =
        if not (state.Host.TrySend(context, command)) then context.Abort()

    // Storage is the source of truth between runs: a write that cannot even be
    // queued means persistence is broken, and the owner stops visibly.
    let private persist state (context: AgentContext<GuildCommand>) write =
        if state.Writer.TrySend(context, write) then true
        else
            state.Logger.LogError("Guild persistence queue is full; stopping the owner")
            context.Abort()
            false

    let private now () = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())

    let private deliver state context (subscriber: Subscription<GuildEvent>) event =
        if not (state.Failed.Contains subscriber.ConnectionId) then
            match subscriber.Events.TryPost event with
            | AgentTryDeliveryResult.Posted -> ()
            | AgentTryDeliveryResult.Closed -> state.Failed.Add subscriber.ConnectionId |> ignore
            | AgentTryDeliveryResult.Full ->
                state.Failed.Add subscriber.ConnectionId |> ignore
                notifyHost state context (SessionHostCommand.SlowConsumer subscriber.ConnectionId)

    let private respond state context connectionId (replyTo: ReliableAgentRef<ChatRoomEvent>) response =
        match replyTo.TryPost response with
        | AgentTryDeliveryResult.Posted | AgentTryDeliveryResult.Closed -> ()
        | AgentTryDeliveryResult.Full -> notifyHost state context (SessionHostCommand.Close(connectionId, "guild_chat_response_overflow"))

    let private subscriberOf state player =
        match state.Online.TryGetValue player with
        | true, connection ->
            match state.Subscribers.TryGetValue connection with
            | true, subscriber -> ValueSome subscriber
            | false, _ -> ValueNone
        | false, _ -> ValueNone

    let private send state context player event =
        subscriberOf state player |> ValueOption.iter (fun subscriber -> deliver state context subscriber event)

    /// To every online member of the guild except one.
    let private broadcast state context (guild: Guild) (except: PlayerId voption) event =
        for membership in guild.Members do
            if ValueSome membership.Player <> except then send state context membership.Player event

    let private profileOf state player =
        match state.Profiles.TryGetValue player with
        | true, profile -> profile
        | false, _ -> PlayerData.create player (Moderation.fallbackUsername player) (Moderation.fallbackDisplayName player) NameColor.unknown

    let private memberView state now (membership: GuildMember) =
        { Membership = { membership with Mute = GuildMember.mute now membership }
          Profile = profileOf state membership.Player
          Online = state.Online.ContainsKey membership.Player }

    // The master first, officers next, then members; each by joining time.
    let private ordered (guild: Guild) =
        guild.Members |> List.sortBy (fun membership -> -(GuildRole.toInt membership.Role), membership.JoinedAt)

    let private guildView state now (guild: Guild) withMessages =
        let messages =
            match state.Chats.TryGetValue guild.Id with
            | true, chat when withMessages -> (Chat.snapshot chat).Messages
            | true, _ | false, _ -> []
        { Guild = guild.Id
          Name = guild.Name
          ChannelId = ChatChannels.ofGuild guild.Id
          CreatedAt = guild.CreatedAt
          Members = ordered guild |> List.map (memberView state now)
          Messages = messages }

    let private inviteView state (invite: GuildInvite) =
        GuildBook.tryFind invite.Guild state.Book
        |> ValueOption.map (fun guild -> { Invite = invite; GuildName = guild.Name })

    let private snapshotOf state player now : GuildState =
        { Guilds = GuildBook.guildsOf player state.Book |> List.sortBy _.CreatedAt |> List.map (fun guild -> guildView state now guild true)
          Invites =
            GuildBook.invitesOf player state.Book
            |> List.filter (fun invite -> now < invite.Expires)
            |> List.choose (inviteView state >> ValueOption.toOption)
          Limits = state.Book.Limits }

    let private summary state (guild: Guild) : GuildSummary =
        { Guild = guild.Id
          Name = guild.Name
          CreatedAt = guild.CreatedAt
          Members = guild.MemberCount
          Master = guild.Master |> ValueOption.map (fun master -> profileOf state master.Player) }

    let private card state now (guild: Guild) : GuildCard =
        { Summary = summary state guild
          Members = ordered guild |> List.map (memberView state now)
          Invites =
            guild.Invites
            |> List.sortBy _.CreatedAt
            |> List.map (fun invite ->
                let profile = match state.Profiles.TryGetValue invite.Player with | true, profile -> ValueSome profile | false, _ -> ValueNone
                invite, profile) }

    let private rejection error : RequestRejection =
        let refuse code message field = { Code = code; Message = message; Field = field }
        match error with
        | GuildError.NameTaken -> refuse RequestRejectionCode.GuildNameTaken "A guild with this name exists." "name"
        | GuildError.ServerFull -> refuse RequestRejectionCode.GuildServerLimit "The server has the maximum number of guilds." ""
        | GuildError.PlayerLimit -> refuse RequestRejectionCode.GuildPlayerLimit "The player is in the maximum number of guilds." "player_id"
        | GuildError.GuildFull -> refuse RequestRejectionCode.GuildFull "The guild has the maximum number of members." ""
        | GuildError.InvitesFull -> refuse RequestRejectionCode.GuildInvitesFull "The guild has the maximum number of pending invitations." ""
        | GuildError.NotFound -> refuse RequestRejectionCode.TargetNotFound "No such guild." "guild_id"
        | GuildError.TargetNotFound -> refuse RequestRejectionCode.TargetNotFound "No such member or invitation." "player_id"
        | GuildError.AlreadyMember -> refuse RequestRejectionCode.GuildAlreadyMember "The player is in the guild already." "player_id"
        | GuildError.AlreadyInvited -> refuse RequestRejectionCode.GuildAlreadyInvited "The player has an invitation to the guild already." "player_id"
        | GuildError.NotPermitted -> refuse RequestRejectionCode.NotPermitted "The guild role does not allow this." ""
        | GuildError.MasterStays -> refuse RequestRejectionCode.GuildMasterStays "Hand the master's role over or disband the guild first." ""

    let private player id = PlayerId.value id
    let private guildKey id = GuildId.value id

    let private chatOf state (guild: Guild) =
        match state.Chats.TryGetValue guild.Id with
        | true, chat -> chat
        | false, _ ->
            match Chat.create (ChatChannels.ofGuild guild.Id) ChatChannelKind.Guild state.Options.HistoryCapacity with
            | Error error -> invalidOp $"Guild chat cannot start: %A{error}"
            | Ok chat ->
                for membership in guild.Members do Chat.join membership.Player chat |> ignore
                state.Chats[guild.Id] <- chat
                chat

    let private joinChat state guild playerId =
        match state.Chats.TryGetValue guild with
        | true, chat -> Chat.join playerId chat |> ignore
        | false, _ -> ()

    let private leaveChat state guild playerId =
        match state.Chats.TryGetValue guild with
        | true, chat -> Chat.leave playerId chat |> ignore
        | false, _ -> ()

    /// Everyone in and invited to a disbanded guild hears of it; its chat goes.
    let private disbanded state context (gone: DisbandedGuild) =
        if persist state context (GuildWrite.Delete gone.Guild) then
            state.Chats.Remove gone.Guild |> ignore
            state.NextMessageIds.Remove gone.Guild |> ignore
            for membership in gone.Members do
                send state context membership.Player (GuildEvent.Changed(GuildChange.Removed(gone.Guild, GuildRemoval.Disbanded)))
            for invite in gone.Invites do
                send state context invite.Player (GuildEvent.Changed(GuildChange.InviteRemoved gone.Guild))
            true
        else false

    let private handedOver state context now (guild: Guild) (previous: GuildMember voption) (master: GuildMember) =
        if persist state context (GuildWrite.TransferMaster(guild.Id, previous, master)) then
            previous |> ValueOption.iter (fun officer ->
                broadcast state context guild ValueNone (GuildEvent.Changed(GuildChange.MemberChanged(guild.Id, memberView state now officer))))
            broadcast state context guild ValueNone (GuildEvent.Changed(GuildChange.MemberChanged(guild.Id, memberView state now master)))
            true
        else false

    let private create state context (subscriber: Subscription<GuildEvent>) requestId raw =
        let actor = subscriber.Profile.PlayerId
        let limits = state.Book.Limits
        let reply event = deliver state context subscriber event
        let invalid message = reply (GuildEvent.Refused(requestId, { Code = RequestRejectionCode.InvalidRequest; Message = message; Field = "name" }))
        match GuildName.create limits.NameMinLength limits.NameMaxLength raw with
        | Error(DomainError.InvalidText(_, TextError.TooShort minimum)) -> invalid $"A guild name has at least {minimum} characters."
        | Error(DomainError.InvalidText(_, TextError.TooLong maximum)) -> invalid $"A guild name has at most {maximum} characters."
        | Error _ -> invalid "A guild name has letters, digits and spaces only."
        | Ok name when not (Moderation.allows state.Moderation (GuildName.value name)) ->
            reply (GuildEvent.Refused(requestId, { Code = RequestRejectionCode.TextNotAllowed; Message = "The guild name contains words that are not allowed."; Field = "name" }))
        | Ok name ->
            match GuildId.create state.NextId with
            | Error _ -> context.Abort()
            | Ok id ->
                let created = now ()
                match GuildBook.create id name actor created state.Book with
                | Error error -> reply (GuildEvent.Refused(requestId, rejection error))
                | Ok(guild, master) ->
                    state.NextId <- state.NextId + 1UL
                    state.Profiles[actor] <- subscriber.Profile
                    if persist state context (GuildWrite.Create(id, name, created, master)) then
                        state.Logger.LogInformation("Guild {GuildId} {Name} created by player {PlayerId}", guildKey id, GuildName.value name, player actor)
                        reply (GuildEvent.Changed(GuildChange.Added(guildView state created guild true)))
                        reply (GuildEvent.Done(requestId, id))

    let private act state context (request: GuildRequest) =
        match state.Subscribers.TryGetValue request.ConnectionId with
        | false, _ -> ()
        | true, subscriber ->
            let actor = subscriber.Profile.PlayerId
            let at = now ()
            let reply event = deliver state context subscriber event
            let refuse error = reply (GuildEvent.Refused(request.RequestId, rejection error))
            let finish guild = reply (GuildEvent.Done(request.RequestId, guild))
            let changed guild view = GuildEvent.Changed(GuildChange.MemberChanged(guild, view))
            match request.Action with
            | GuildAction.Create raw -> create state context subscriber request.RequestId raw
            | GuildAction.Invite(guild, target) ->
                match subscriberOf state target with
                | ValueNone when target <> actor ->
                    reply (GuildEvent.Refused(request.RequestId, { Code = RequestRejectionCode.TargetNotFound; Message = "The player is not online."; Field = "player_id" }))
                | ValueSome _ | ValueNone ->
                    match GuildBook.invite actor guild target at state.Book with
                    | Error error -> refuse error
                    | Ok invitation ->
                        subscriberOf state target |> ValueOption.iter (fun invited -> state.Profiles[target] <- invited.Profile)
                        if persist state context (GuildWrite.PutInvite invitation) then
                            state.Logger.LogInformation("Player {PlayerId} invited player {Target} to guild {GuildId}", player actor, player target, guildKey guild)
                            inviteView state invitation |> ValueOption.iter (fun view -> send state context target (GuildEvent.Changed(GuildChange.Invited view)))
                            finish guild
            | GuildAction.Answer(guild, true) ->
                match GuildBook.accept actor guild at state.Book with
                | Error error -> refuse error
                | Ok(entry, _, joined) ->
                    state.Profiles[actor] <- subscriber.Profile
                    if persist state context (GuildWrite.AcceptInvite(guild, joined)) then
                        joinChat state guild actor
                        state.Logger.LogInformation("Player {PlayerId} joined guild {GuildId}", player actor, guildKey guild)
                        reply (GuildEvent.Changed(GuildChange.InviteRemoved guild))
                        reply (GuildEvent.Changed(GuildChange.Added(guildView state at entry true)))
                        broadcast state context entry (ValueSome actor) (changed guild (memberView state at joined))
                        finish guild
            | GuildAction.Answer(guild, false) ->
                match GuildBook.decline actor guild at state.Book with
                | Error error -> refuse error
                | Ok _ ->
                    if persist state context (GuildWrite.RemoveInvite(guild, actor)) then
                        reply (GuildEvent.Changed(GuildChange.InviteRemoved guild))
                        finish guild
            | GuildAction.Leave guild ->
                match GuildBook.leave actor guild state.Book with
                | Error error -> refuse error
                | Ok(entry, _) ->
                    if persist state context (GuildWrite.RemoveMember(guild, actor)) then
                        leaveChat state guild actor
                        state.Logger.LogInformation("Player {PlayerId} left guild {GuildId}", player actor, guildKey guild)
                        reply (GuildEvent.Changed(GuildChange.Removed(guild, GuildRemoval.Left)))
                        broadcast state context entry ValueNone (GuildEvent.Changed(GuildChange.MemberRemoved(guild, actor, GuildRemoval.Left)))
                        finish guild
            | GuildAction.Exclude(guild, target) ->
                match GuildBook.exclude actor guild target state.Book with
                | Error error -> refuse error
                | Ok(entry, _) ->
                    if persist state context (GuildWrite.RemoveMember(guild, target)) then
                        leaveChat state guild target
                        state.Logger.LogInformation("Player {Target} excluded from guild {GuildId} by player {PlayerId}", player target, guildKey guild, player actor)
                        send state context target (GuildEvent.Changed(GuildChange.Removed(guild, GuildRemoval.Excluded)))
                        broadcast state context entry ValueNone (GuildEvent.Changed(GuildChange.MemberRemoved(guild, target, GuildRemoval.Excluded)))
                        finish guild
            | GuildAction.SetRole(guild, target, role) ->
                match GuildBook.setRole actor guild target role state.Book with
                | Error error -> refuse error
                | Ok(entry, membership) ->
                    if persist state context (GuildWrite.PutMember(guild, membership)) then
                        state.Logger.LogInformation("Player {Target} is now {Role} of guild {GuildId}", player target, GuildRole.key role, guildKey guild)
                        broadcast state context entry ValueNone (changed guild (memberView state at membership))
                        finish guild
            | GuildAction.Transfer(guild, target) ->
                match GuildBook.transfer actor guild target state.Book with
                | Error error -> refuse error
                | Ok(entry, previous, master) ->
                    if handedOver state context at entry previous master then
                        state.Logger.LogInformation("Guild {GuildId} handed to player {Target} by player {PlayerId}", guildKey guild, player target, player actor)
                        finish guild
            | GuildAction.Mute(guild, target, term, reason) ->
                match GuildBook.mute actor guild target term reason at state.Book with
                | Error error -> refuse error
                | Ok(entry, membership) ->
                    if persist state context (GuildWrite.PutMember(guild, membership)) then
                        state.Logger.LogInformation("Player {Target} muted in guild {GuildId} by player {PlayerId}: {Reason}",
                                                    player target, guildKey guild, player actor, SanctionReason.value reason)
                        broadcast state context entry ValueNone (changed guild (memberView state at membership))
                        finish guild
            | GuildAction.Unmute(guild, target) ->
                match GuildBook.unmute actor guild target at state.Book with
                | Error error -> refuse error
                | Ok(entry, membership) ->
                    if persist state context (GuildWrite.PutMember(guild, membership)) then
                        state.Logger.LogInformation("Guild mute of player {Target} in guild {GuildId} lifted by player {PlayerId}", player target, guildKey guild, player actor)
                        broadcast state context entry ValueNone (changed guild (memberView state at membership))
                        finish guild
            | GuildAction.Disband guild ->
                match GuildBook.disband actor guild state.Book with
                | Error error -> refuse error
                | Ok gone ->
                    if disbanded state context gone then
                        state.Logger.LogInformation("Guild {GuildId} {Name} disbanded by player {PlayerId}", guildKey guild, GuildName.value gone.Name, player actor)
                        finish guild

    let private rejectChat state context connectionId (replyTo: ReliableAgentRef<ChatRoomEvent>) requestId code message field =
        respond state context connectionId replyTo (ChatRoomEvent.Rejected(requestId, { Code = code; Message = message; Field = field }))

    /// A member's message: the guild owner checks membership, the guild mute and
    /// the rate; the session decided the author (the real profile) and the word list.
    let private publish state context guild (request: ChatSubmission) =
        let reject = rejectChat state context request.ConnectionId request.ReplyTo request.RequestId
        match state.Subscribers.TryGetValue request.ConnectionId with
        | true, author when author.Profile.PlayerId <> request.Author.PlayerId ->
            notifyHost state context (SessionHostCommand.Close(request.ConnectionId, "guild_chat_author_mismatch"))
        | false, _ -> reject RequestRejectionCode.NotChannelMember "Player is not a member of this guild." ""
        | true, author ->
            let at = now ()
            match GuildBook.mayWrite author.Profile.PlayerId guild at state.Book with
            | Error GuildError.NotPermitted -> reject RequestRejectionCode.Muted "Muted in this guild: reading only." "text"
            | Error _ -> reject RequestRejectionCode.NotChannelMember "Player is not a member of this guild." ""
            | Ok _ ->
                match RateLimit.admit state.Senders Environment.TickCount64 author.Profile.PlayerId request.Fingerprint with
                | Error RateLimit.Refusal.Repeated -> reject RequestRejectionCode.RateLimited "The same message was sent too recently." "text"
                | Error RateLimit.Refusal.Exhausted -> reject RequestRejectionCode.RateLimited "Too many messages. Wait a moment." "text"
                | Ok () ->
                    match GuildBook.tryFind guild state.Book with
                    | ValueNone -> reject RequestRejectionCode.NotChannelMember "Player is not a member of this guild." ""
                    | ValueSome entry ->
                        let chat = chatOf state entry
                        Chat.join author.Profile.PlayerId chat |> ignore
                        let next = match state.NextMessageIds.TryGetValue guild with | true, id -> id | false, _ -> 1UL
                        match ChatMessageId.create next with
                        | Error _ -> context.Abort()
                        | Ok messageId ->
                            let message =
                                ChatMessage.create messageId chat.ChannelId request.Author request.CharacterName request.Text at
                                |> ChatMessage.withFlagged request.Flagged
                            match Chat.append message chat with
                            | Error _ -> context.Abort()
                            | Ok () ->
                                state.NextMessageIds[guild] <- next + 1UL
                                respond state context request.ConnectionId request.ReplyTo (ChatRoomEvent.Accepted(request.RequestId, message))
                                broadcast state context entry (ValueSome author.Profile.PlayerId) (GuildEvent.Chat(ChatRoomEvent.Published message))

    /// The master removes any message of the guild chat, an officer a member's.
    let private removeMessage state context guild (request: ChatRemoval) =
        let reject = rejectChat state context request.ConnectionId request.ReplyTo request.RequestId
        match state.Subscribers.TryGetValue request.ConnectionId, state.Chats.TryGetValue guild with
        | (true, remover), (true, chat) ->
            match (Chat.snapshot chat).Messages |> List.tryFind (fun message -> message.MessageId = request.MessageId) with
            | None -> reject RequestRejectionCode.TargetNotFound "No such message in the channel." "message_id"
            | Some message ->
                let author = message.Author |> ValueOption.map _.PlayerId
                match GuildBook.mayRemoveMessage remover.Profile.PlayerId guild author state.Book with
                | Error GuildError.NotPermitted -> reject RequestRejectionCode.NotPermitted "The guild role does not allow removing this message." ""
                | Error _ -> reject RequestRejectionCode.NotChannelMember "Player is not a member of this guild." ""
                | Ok entry ->
                    match Chat.remove request.MessageId chat with
                    | ValueNone -> reject RequestRejectionCode.TargetNotFound "No such message in the channel." "message_id"
                    | ValueSome removed ->
                        state.Logger.LogInformation("Message {MessageId} of guild {GuildId} removed by player {PlayerId}",
                                                    ChatMessageId.value removed.MessageId, guildKey guild, player remover.Profile.PlayerId)
                        broadcast state context entry (ValueSome remover.Profile.PlayerId) (GuildEvent.Chat(ChatRoomEvent.Removed(ValueNone, removed)))
                        respond state context request.ConnectionId request.ReplyTo (ChatRoomEvent.Removed(ValueSome request.RequestId, removed))
        | (true, _), (false, _) -> reject RequestRejectionCode.TargetNotFound "No such message in the channel." "message_id"
        | (false, _), _ -> ()

    let private rename state context (profile: PlayerData) =
        if state.Profiles.ContainsKey profile.PlayerId then
            state.Profiles[profile.PlayerId] <- profile
            let at = now ()
            for guild in GuildBook.guildsOf profile.PlayerId state.Book do
                guild.Member profile.PlayerId |> ValueOption.iter (fun membership ->
                    broadcast state context guild ValueNone
                        (GuildEvent.Changed(GuildChange.MemberChanged(guild.Id, memberView state at membership))))

    /// Guildmates see the member come online or go offline.
    let private announcePresence state context playerId =
        let at = now ()
        for guild in GuildBook.guildsOf playerId state.Book do
            guild.Member playerId |> ValueOption.iter (fun membership ->
                broadcast state context guild (ValueSome playerId)
                    (GuildEvent.Changed(GuildChange.MemberChanged(guild.Id, memberView state at membership))))

    let private join state context (subscription: Subscription<GuildEvent>) =
        let playerId = subscription.Profile.PlayerId
        // The runtime frees an account only after this owner acknowledged its
        // detach, so another connection of the same account is a broken invariant.
        match state.Online.TryGetValue playerId with
        | true, previous when previous <> subscription.ConnectionId ->
            notifyHost state context (SessionHostCommand.Close(subscription.ConnectionId, "guilds_identity_conflict"))
        | true, _ | false, _ ->
            state.Subscribers[subscription.ConnectionId] <- subscription
            state.Online[playerId] <- subscription.ConnectionId
            state.Failed.Remove subscription.ConnectionId |> ignore
            if state.Profiles.ContainsKey playerId then state.Profiles[playerId] <- subscription.Profile
            deliver state context subscription (GuildEvent.Snapshot(snapshotOf state playerId (now ())))
            announcePresence state context playerId

    let private detach state (context: AgentContext<GuildCommand>) (request: SessionDetach) =
        match state.Subscribers.TryGetValue request.ConnectionId with
        | true, subscriber ->
            let playerId = subscriber.Profile.PlayerId
            state.Subscribers.Remove request.ConnectionId |> ignore
            state.Failed.Remove request.ConnectionId |> ignore
            match state.Online.TryGetValue playerId with
            | true, owner when owner = request.ConnectionId ->
                state.Online.Remove playerId |> ignore
                announcePresence state context playerId
            | true, _ | false, _ -> ()
        | false, _ -> ()
        match request.ReplyTo.TryPost request.ConnectionId with
        | AgentTryDeliveryResult.Posted | AgentTryDeliveryResult.Closed -> ()
        | AgentTryDeliveryResult.Full -> context.Abort()

    /// Invitations and guild mutes end by themselves; this makes it visible.
    let private expire state context =
        let at = now ()
        let mutable admitted = true
        for invite in GuildBook.expireInvites at state.Book do
            if admitted then
                admitted <- persist state context (GuildWrite.RemoveInvite(invite.Guild, invite.Player))
                if admitted then send state context invite.Player (GuildEvent.Changed(GuildChange.InviteRemoved invite.Guild))
        if admitted then
            for guild, membership in GuildBook.expireMutes at state.Book do
                if admitted then
                    admitted <- persist state context (GuildWrite.PutMember(guild.Id, membership))
                    if admitted then broadcast state context guild ValueNone (GuildEvent.Changed(GuildChange.MemberChanged(guild.Id, memberView state at membership)))

    let private search state (query: string) page =
        let text = query.Trim()
        let matches =
            match UInt64.TryParse text with
            | true, raw ->
                match GuildId.create raw |> Result.toOption |> Option.bind (fun id -> GuildBook.tryFind id state.Book |> ValueOption.toOption) with
                | Some guild -> [ guild ]
                | None -> []
            | false, _ ->
                let needle = text.ToLowerInvariant()
                GuildBook.all state.Book
                |> List.filter (fun guild -> needle.Length = 0 || (GuildName.value guild.Name).ToLowerInvariant().Contains needle)
        let sorted = matches |> List.sortBy (fun guild -> GuildName.key guild.Name)
        let page = max 1 page
        { Guilds = sorted |> List.skip (min sorted.Length ((page - 1) * GuildPage.Size)) |> List.truncate GuildPage.Size |> List.map (summary state)
          Total = sorted.Length
          Page = page }

    let private admin state context command (reply: ReplyChannel<GuildAdminResult>) =
        let at = now ()
        match command with
        | GuildAdminCommand.Search(query, page) -> reply.Reply(GuildAdminResult.Page(search state query page))
        | GuildAdminCommand.Card guild ->
            reply.Reply(GuildAdminResult.Card(GuildBook.tryFind guild state.Book |> ValueOption.map (card state at)))
        | GuildAdminCommand.PlayerGuilds playerId ->
            GuildBook.guildsOf playerId state.Book
            |> List.choose (fun guild -> guild.Member playerId |> ValueOption.map (fun membership -> summary state guild, membership.Role) |> ValueOption.toOption)
            |> List.sortBy (fun (summary, _) -> GuildName.key summary.Name)
            |> GuildAdminResult.PlayerGuilds
            |> reply.Reply
        | GuildAdminCommand.Appoint(guild, target) ->
            match GuildBook.appoint guild target state.Book with
            | Error error -> reply.Reply(GuildAdminResult.Refused error)
            | Ok(entry, previous, master) ->
                if handedOver state context at entry previous master then
                    state.Logger.LogInformation("Player {Target} appointed master of guild {GuildId} from the panel", player target, guildKey guild)
                    reply.Reply(GuildAdminResult.Appointed(card state at entry))
        | GuildAdminCommand.Dissolve guild ->
            match GuildBook.dissolve guild state.Book with
            | Error error -> reply.Reply(GuildAdminResult.Refused error)
            | Ok gone ->
                if disbanded state context gone then
                    state.Logger.LogInformation("Guild {GuildId} {Name} disbanded from the panel", guildKey guild, GuildName.value gone.Name)
                    reply.Reply(GuildAdminResult.Dissolved gone)

    let private schedule state context =
        if state.Ticker.IsNone then
            state.Ticker <- Some(AgentTicker.start (TimeSpan.FromMilliseconds(float state.Options.InviteCheckIntervalMs)) context GuildCommand.Expire)

    let private handle state (context: AgentContext<GuildCommand>) command = task {
        schedule state context
        match command with
        | GuildCommand.Join subscription -> join state context subscription
        | GuildCommand.Act request -> act state context request
        | GuildCommand.Publish(guild, submission) -> publish state context guild submission
        | GuildCommand.Remove(guild, removal) -> removeMessage state context guild removal
        | GuildCommand.Rename profile -> rename state context profile
        | GuildCommand.Admin(command, reply) -> admin state context command reply
        | GuildCommand.Expire _ ->
            expire state context
            state.Ticker |> Option.iter _.Acknowledge()
        | GuildCommand.Detach request -> detach state context request
    }

    let private isControl = function
        | GuildCommand.Join _ | GuildCommand.Expire _ | GuildCommand.Detach _ -> true
        | GuildCommand.Act _ | GuildCommand.Publish _ | GuildCommand.Remove _ | GuildCommand.Rename _ | GuildCommand.Admin _ -> false

    /// The options and limits come checked by GameSettings.create; the stored
    /// guilds stay as they are, limits lowered since bind nobody.
    let start (options: GuildOptions) (limits: GuildLimits) (rules: ModerationRules) (chatRate: RateLimitOptions)
              (persistence: GuildPersistence) (host: ReliableAgentRef<SessionHostCommand>) (logger: ILogger) =
        let highest = persistence.Loaded |> List.fold (fun highest guild -> max highest (GuildId.value guild.Id)) 0UL
        let names = persistence.Loaded |> List.map (fun guild -> GuildName.key guild.Name)
        if highest >= max persistence.NextId 1UL then
            Error(sprintf "Stored guild ID %d is not below the next ID %d." highest persistence.NextId)
        elif (List.distinct names).Length <> names.Length then Error "Stored guilds repeat a name."
        else
            let state = {
                Options = options
                Book = GuildBook.restore limits persistence.Loaded
                Moderation = rules
                Profiles = Dictionary()
                Chats = Dictionary()
                NextMessageIds = Dictionary()
                Subscribers = Dictionary()
                Online = Dictionary()
                Failed = HashSet()
                Senders = RateLimit.create chatRate
                Writer = AgentOutbox(options.MaxPendingWrites, persistence.Writer)
                Host = AgentOutbox(options.MaxControlDeliveries, host)
                Logger = logger
                NextId = max persistence.NextId 1UL
                Ticker = None
            }
            for profile in persistence.Profiles do state.Profiles[profile.PlayerId] <- profile
            let settings = {
                AgentOptions.create "guilds" with
                    Mailbox = AgentMailbox.boundedWithControl options.MailboxCapacity options.ControlReserve
            }
            let agent = Agent.Start(settings, handle state, isControl = isControl)
            let tick = System.Diagnostics.Stopwatch.GetTimestamp()
            agent.TryPost(GuildCommand.Expire { DueTimestamp = tick; QueuedTimestamp = tick }) |> ignore
            Ok agent
