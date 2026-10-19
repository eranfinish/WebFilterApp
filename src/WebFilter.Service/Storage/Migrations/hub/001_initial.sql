-- Hub: the family's source of truth. Settings are one versioned JSON document (FamilyBundle);
-- what grows over time (usage, events, requests) is relational.
CREATE TABLE config (
    version     INTEGER PRIMARY KEY,
    bundle_json TEXT    NOT NULL,
    saved_at    TEXT    NOT NULL
);

CREATE TABLE runtime (
    member_id      TEXT PRIMARY KEY,
    paused_until   TEXT,
    homework_until TEXT,
    grants_json    TEXT NOT NULL
);

-- minute = whole minutes since the Unix epoch (UTC)
CREATE TABLE usage_minutes (
    member_id TEXT    NOT NULL,
    device_id TEXT    NOT NULL,
    minute    INTEGER NOT NULL,
    PRIMARY KEY (member_id, device_id, minute)
) WITHOUT ROWID;

CREATE TABLE events (
    seq       INTEGER PRIMARY KEY,
    ts        TEXT    NOT NULL,
    type      TEXT    NOT NULL,
    member_id TEXT,
    device_id TEXT,
    reason    TEXT,
    url       TEXT,
    app       TEXT,
    remaining INTEGER,
    detail    TEXT
);
CREATE INDEX ix_events_ts ON events (ts);

CREATE TABLE access_requests (
    id              TEXT PRIMARY KEY,
    member_id       TEXT NOT NULL,
    device_id       TEXT NOT NULL,
    kind            TEXT NOT NULL,
    target          TEXT NOT NULL,
    target_key      TEXT NOT NULL,
    minutes         INTEGER,
    message         TEXT,
    created_at      TEXT NOT NULL,
    status          TEXT NOT NULL,
    decided_at      TEXT,
    created_rule_id TEXT
);

CREATE TABLE counters (
    name  TEXT PRIMARY KEY,
    value INTEGER NOT NULL
);
