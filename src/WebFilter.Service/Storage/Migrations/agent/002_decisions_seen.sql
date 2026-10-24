-- The request decisions the hub's last snapshot carried (its last 48 hours), by request id and decision time: each one is
-- announced to the member's browsers once, and a restart does not announce it again (MVP-PLAN §8.1).
CREATE TABLE decisions_seen (
    request_id TEXT PRIMARY KEY,
    decided_at TEXT NOT NULL
);
