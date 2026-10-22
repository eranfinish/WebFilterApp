-- A device that revoked itself (POST /v1/unregister) keeps why: "uninstall". NULL when a parent revoked it.
ALTER TABLE devices ADD COLUMN revoked_reason TEXT;
