-- A YouTube request's channel names as the child's browser saw them, and the channel search it came from
-- (PROTECTION-SPEC §15). NULL for other kinds and for requests from older extensions.
ALTER TABLE access_requests ADD COLUMN title TEXT;
ALTER TABLE access_requests ADD COLUMN handle TEXT;
ALTER TABLE access_requests ADD COLUMN search_query TEXT;
