-- Parent notifications for the notifier app on the hub computer (PROTECTION-SPEC §10).
-- id is stable per notification (inserting it again does nothing); at = UTC ticks when the hub recorded it, strictly
-- increasing, so a client's "since" cursor never misses one that arrived late.
CREATE TABLE notifications (
    seq       INTEGER PRIMARY KEY,
    id        TEXT    NOT NULL UNIQUE,
    kind      TEXT    NOT NULL,
    at        INTEGER NOT NULL,
    member_id TEXT,
    device_id TEXT,
    detail    TEXT
);
CREATE INDEX ix_notifications_at ON notifications (at);
