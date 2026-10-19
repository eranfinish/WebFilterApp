-- Agent: the last snapshot from the hub, its own usage, and what still has to reach the hub.
CREATE TABLE snapshot (
    id           INTEGER PRIMARY KEY CHECK (id = 1),
    version      INTEGER NOT NULL,
    bundle_json  TEXT    NOT NULL,
    runtime_json TEXT    NOT NULL
);

-- Usage known from the hub (all devices); minute = whole minutes since the Unix epoch (UTC)
CREATE TABLE shared_usage (
    member_id TEXT    NOT NULL,
    device_id TEXT    NOT NULL,
    minute    INTEGER NOT NULL,
    PRIMARY KEY (member_id, device_id, minute)
) WITHOUT ROWID;

-- Usage recorded on this device
CREATE TABLE local_usage (
    member_id TEXT    NOT NULL,
    minute    INTEGER NOT NULL,
    PRIMARY KEY (member_id, minute)
) WITHOUT ROWID;

CREATE TABLE local_pauses (
    member_id TEXT PRIMARY KEY,
    until     TEXT NOT NULL
);

CREATE TABLE outbox (
    seq        INTEGER PRIMARY KEY,
    event_json TEXT NOT NULL
);

CREATE TABLE queued_requests (
    id           TEXT PRIMARY KEY,
    request_json TEXT NOT NULL
);

CREATE TABLE counters (
    name  TEXT PRIMARY KEY,
    value INTEGER NOT NULL
);
