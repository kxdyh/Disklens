-- Prefixes are covered by the fresh-evidence and cursor indexes. This only
-- changes index storage; it does not remove any contribution or receipt row.
DROP INDEX contributions_installation;
DROP INDEX contributions_bucket_active;
