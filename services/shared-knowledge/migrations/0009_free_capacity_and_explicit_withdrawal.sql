-- Storage is bounded by the existing D1 Free database's native 500 MB limit,
-- not arbitrary beta row counts. No data is deleted or given an expiry.
DROP TRIGGER installations_capacity;
DROP TRIGGER buckets_capacity;
DROP TRIGGER contributions_capacity;
DROP TRIGGER analysis_receipts_capacity;
DROP TRIGGER feedback_capacity;
DROP TRIGGER usage_limits_capacity;
DROP TRIGGER contributions_installation_capacity;

-- Retain accounting and its insert/delete triggers for operator visibility.
-- This legacy column no longer enforces storage; native D1 bytes do.
UPDATE resource_caps SET maximum=9007199254740991;

-- Explicit withdrawal of all past contributions is an O(1) epoch barrier.
-- It takes effect before bounded physical cleanup, including for old clients.
ALTER TABLE installations ADD COLUMN revoked_through_epoch INTEGER NOT NULL DEFAULT -1
 CHECK(revoked_through_epoch>=-1);
CREATE INDEX contributions_installation_epoch_cursor ON contributions(installation_id,epoch,id);
