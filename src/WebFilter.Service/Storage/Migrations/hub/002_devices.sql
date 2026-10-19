-- M6: paired devices and event ids already received from agents.
-- Pairing codes are short-lived and kept in memory only.
CREATE TABLE devices (
    id               TEXT PRIMARY KEY,
    name             TEXT NOT NULL,
    os               TEXT NOT NULL,
    cert_thumbprint  TEXT NOT NULL UNIQUE,
    paired_at        TEXT NOT NULL,
    last_seen_at     TEXT,
    revoked_at       TEXT
);

CREATE TABLE seen_events (
    device_id TEXT NOT NULL,
    event_id  TEXT NOT NULL,
    seen_at   TEXT NOT NULL,
    PRIMARY KEY (device_id, event_id)
) WITHOUT ROWID;
