-- Historical design example; not the current Dreamsleeve contract.
-- See docs/CurrentStateRu.md and the README in this example directory.
CREATE INDEX IF NOT EXISTS ix_players_created_at_ms
    ON players(created_at_ms);

CREATE UNIQUE INDEX IF NOT EXISTS ux_players_normalized_nickname_active
    ON players(normalized_nickname)
    WHERE is_deleted = 0;

CREATE UNIQUE INDEX IF NOT EXISTS ux_player_identities_active_per_player
    ON player_identities(player_id)
    WHERE is_active = 1;

CREATE INDEX IF NOT EXISTS ix_player_identities_player
    ON player_identities(player_id, key_version DESC);

CREATE INDEX IF NOT EXISTS ix_player_sessions_player
    ON player_sessions(player_id);

CREATE INDEX IF NOT EXISTS ix_player_sessions_expiry
    ON player_sessions(expires_at_ms, revoked_at_ms);

CREATE UNIQUE INDEX IF NOT EXISTS ux_friend_requests_pending_pair
    ON friend_requests(from_player_id, to_player_id)
    WHERE status = 0;

CREATE INDEX IF NOT EXISTS ix_friend_requests_to_status
    ON friend_requests(to_player_id, status, created_at_ms DESC);

CREATE INDEX IF NOT EXISTS ix_friendships_high
    ON friendships(player_high_id);

CREATE INDEX IF NOT EXISTS ix_player_blocks_blocked
    ON player_blocks(blocked_player_id);

CREATE INDEX IF NOT EXISTS ix_party_members_party_order
    ON party_members(party_id, join_order);

CREATE UNIQUE INDEX IF NOT EXISTS ux_party_invites_pending_target
    ON party_invites(party_id, invited_player_id)
    WHERE status = 0;

CREATE INDEX IF NOT EXISTS ix_party_invites_target_status
    ON party_invites(invited_player_id, status, created_at_ms DESC);

CREATE UNIQUE INDEX IF NOT EXISTS ux_guilds_normalized_name_active
    ON guilds(normalized_name)
    WHERE status = 0;

CREATE INDEX IF NOT EXISTS ix_guild_memberships_player
    ON guild_memberships(player_id, joined_at_ms DESC);

CREATE UNIQUE INDEX IF NOT EXISTS ux_guild_one_master
    ON guild_memberships(guild_id)
    WHERE role = 2;

CREATE UNIQUE INDEX IF NOT EXISTS ux_guild_invites_pending_target
    ON guild_invites(guild_id, invited_player_id)
    WHERE status = 0;

CREATE INDEX IF NOT EXISTS ix_guild_invites_target_status
    ON guild_invites(invited_player_id, status, created_at_ms DESC);

CREATE INDEX IF NOT EXISTS ix_global_sanctions_player
    ON global_sanctions(player_id, sanction_type, issued_at_ms DESC);

CREATE INDEX IF NOT EXISTS ix_text_filter_terms_mode
    ON text_filter_terms(mode);

CREATE UNIQUE INDEX IF NOT EXISTS ux_text_filter_terms_normalized
    ON text_filter_terms(normalized_term);

CREATE INDEX IF NOT EXISTS ix_scheduled_announcements_enabled
    ON scheduled_announcements(is_enabled);

CREATE INDEX IF NOT EXISTS ix_audit_events_occurred_at
    ON audit_events(occurred_at_ms DESC);

CREATE INDEX IF NOT EXISTS ix_audit_events_actor
    ON audit_events(actor_player_id, occurred_at_ms DESC);

CREATE INDEX IF NOT EXISTS ix_audit_events_subject
    ON audit_events(subject_player_id, occurred_at_ms DESC);
