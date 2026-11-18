-- Bounded beta storage: no scheduled expiry or physical retention deletion.
-- Raising these limits is an explicit operator decision, never a client field.
CREATE TABLE resource_caps (
 name TEXT PRIMARY KEY, used INTEGER NOT NULL CHECK(used>=0),
 maximum INTEGER NOT NULL CHECK(maximum>=0)
);
CREATE TABLE service_budgets (
 lane TEXT PRIMARY KEY CHECK(lane IN ('public','management')), day TEXT NOT NULL,
 requests INTEGER NOT NULL DEFAULT 0, read_units INTEGER NOT NULL DEFAULT 0,
 write_units INTEGER NOT NULL DEFAULT 0
);

INSERT INTO resource_caps(name,used,maximum) SELECT 'installations',COUNT(*),5000 FROM installations;
CREATE TRIGGER installations_capacity BEFORE INSERT ON installations
 WHEN NOT EXISTS(SELECT 1 FROM installations WHERE id=NEW.id)
 AND (SELECT used>=maximum FROM resource_caps WHERE name='installations')
 BEGIN SELECT RAISE(ABORT,'shared_capacity_limit'); END;
CREATE TRIGGER installations_count_insert AFTER INSERT ON installations
 BEGIN UPDATE resource_caps SET used=used+1 WHERE name='installations'; END;
CREATE TRIGGER installations_count_delete AFTER DELETE ON installations
 BEGIN UPDATE resource_caps SET used=used-1 WHERE name='installations'; END;

INSERT INTO resource_caps(name,used,maximum) SELECT 'buckets',COUNT(*),2000 FROM buckets;
CREATE TRIGGER buckets_capacity BEFORE INSERT ON buckets
 WHEN NOT EXISTS(SELECT 1 FROM buckets WHERE key=NEW.key)
 AND (SELECT used>=maximum FROM resource_caps WHERE name='buckets')
 BEGIN SELECT RAISE(ABORT,'shared_capacity_limit'); END;
CREATE TRIGGER buckets_count_insert AFTER INSERT ON buckets
 BEGIN UPDATE resource_caps SET used=used+1 WHERE name='buckets'; END;
CREATE TRIGGER buckets_count_delete AFTER DELETE ON buckets
 BEGIN UPDATE resource_caps SET used=used-1 WHERE name='buckets'; END;

INSERT INTO resource_caps(name,used,maximum) SELECT 'contributions',COUNT(*),20000 FROM contributions;
CREATE TRIGGER contributions_capacity BEFORE INSERT ON contributions
 WHEN NOT EXISTS(SELECT 1 FROM contributions WHERE id=NEW.id)
 AND (SELECT used>=maximum FROM resource_caps WHERE name='contributions')
 BEGIN SELECT RAISE(ABORT,'shared_capacity_limit'); END;
CREATE TRIGGER contributions_count_insert AFTER INSERT ON contributions
 BEGIN UPDATE resource_caps SET used=used+1 WHERE name='contributions'; END;
CREATE TRIGGER contributions_count_delete AFTER DELETE ON contributions
 BEGIN UPDATE resource_caps SET used=used-1 WHERE name='contributions'; END;

INSERT INTO resource_caps(name,used,maximum) SELECT 'analysis_receipts',COUNT(*),50000 FROM analysis_receipts;
CREATE TRIGGER analysis_receipts_capacity BEFORE INSERT ON analysis_receipts
 WHEN NOT EXISTS(SELECT 1 FROM analysis_receipts WHERE installation_id=NEW.installation_id AND analysis_id=NEW.analysis_id)
 AND (SELECT used>=maximum FROM resource_caps WHERE name='analysis_receipts')
 BEGIN SELECT RAISE(ABORT,'shared_capacity_limit'); END;
CREATE TRIGGER analysis_receipts_count_insert AFTER INSERT ON analysis_receipts
 BEGIN UPDATE resource_caps SET used=used+1 WHERE name='analysis_receipts'; END;
CREATE TRIGGER analysis_receipts_count_delete AFTER DELETE ON analysis_receipts
 BEGIN UPDATE resource_caps SET used=used-1 WHERE name='analysis_receipts'; END;

INSERT INTO resource_caps(name,used,maximum) SELECT 'feedback',COUNT(*),2000 FROM feedback;
CREATE TRIGGER feedback_capacity BEFORE INSERT ON feedback
 WHEN NOT EXISTS(SELECT 1 FROM feedback WHERE installation_id=NEW.installation_id AND bucket_key=NEW.bucket_key AND reason=NEW.reason)
 AND (SELECT used>=maximum FROM resource_caps WHERE name='feedback')
 BEGIN SELECT RAISE(ABORT,'shared_capacity_limit'); END;
CREATE TRIGGER feedback_count_insert AFTER INSERT ON feedback
 BEGIN UPDATE resource_caps SET used=used+1 WHERE name='feedback'; END;
CREATE TRIGGER feedback_count_delete AFTER DELETE ON feedback
 BEGIN UPDATE resource_caps SET used=used-1 WHERE name='feedback'; END;

INSERT INTO resource_caps(name,used,maximum) SELECT 'usage_limits',COUNT(*),8192 FROM usage_limits;
CREATE TRIGGER usage_limits_capacity BEFORE INSERT ON usage_limits
 WHEN NOT EXISTS(SELECT 1 FROM usage_limits WHERE key=NEW.key)
 AND (SELECT used>=maximum FROM resource_caps WHERE name='usage_limits')
 BEGIN SELECT RAISE(ABORT,'shared_capacity_limit'); END;
CREATE TRIGGER usage_limits_count_insert AFTER INSERT ON usage_limits
 BEGIN UPDATE resource_caps SET used=used+1 WHERE name='usage_limits'; END;
CREATE TRIGGER usage_limits_count_delete AFTER DELETE ON usage_limits
 BEGIN UPDATE resource_caps SET used=used-1 WHERE name='usage_limits'; END;

-- Bound a user's all-withdraw transaction and the work of its indexed query.
CREATE TRIGGER contributions_installation_capacity BEFORE INSERT ON contributions
 WHEN NOT EXISTS(SELECT 1 FROM contributions WHERE installation_id=NEW.installation_id AND bucket_key=NEW.bucket_key)
 AND (SELECT COUNT(*) FROM contributions WHERE installation_id=NEW.installation_id)>=512
 BEGIN SELECT RAISE(ABORT,'shared_capacity_limit'); END;
-- Cover cursor pagination without scanning/sorting every contribution for a user.
CREATE INDEX contributions_installation_cursor ON contributions(installation_id,withdrawn,id);
