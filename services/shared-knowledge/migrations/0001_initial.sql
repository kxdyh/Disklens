CREATE TABLE installations (
 id TEXT PRIMARY KEY, token_hash TEXT NOT NULL, created_at INTEGER NOT NULL,
 epoch INTEGER NOT NULL DEFAULT 0, enabled INTEGER NOT NULL DEFAULT 0,
 agreement_version TEXT NOT NULL DEFAULT '', share_history INTEGER NOT NULL DEFAULT 0,
 policy_revision TEXT NOT NULL DEFAULT '', consent_at INTEGER
);
CREATE TABLE buckets (
 key TEXT PRIMARY KEY, software_id TEXT NOT NULL, version_major TEXT NOT NULL,
 role_id TEXT NOT NULL, shape TEXT NOT NULL, revision INTEGER NOT NULL DEFAULT 1,
 updated_at INTEGER NOT NULL
);
CREATE TABLE contributions (
 id TEXT PRIMARY KEY, installation_id TEXT NOT NULL REFERENCES installations(id),
 analysis_id TEXT NOT NULL, bucket_key TEXT NOT NULL REFERENCES buckets(key),
 purpose TEXT NOT NULL, action TEXT NOT NULL, source TEXT NOT NULL,
 provider_class TEXT NOT NULL, model_family TEXT NOT NULL,
 default_prompt INTEGER NOT NULL, shared_context INTEGER NOT NULL,
 epoch INTEGER NOT NULL, first_observed INTEGER NOT NULL, updated_at INTEGER NOT NULL,
 withdrawn INTEGER NOT NULL DEFAULT 0,
 UNIQUE(installation_id, bucket_key), UNIQUE(installation_id, analysis_id)
);
CREATE INDEX contributions_bucket_active ON contributions(bucket_key, withdrawn, installation_id);
CREATE INDEX contributions_installation ON contributions(installation_id, withdrawn);
CREATE TABLE usage_limits (key TEXT PRIMARY KEY, count INTEGER NOT NULL);
CREATE TABLE feedback (
 installation_id TEXT NOT NULL, bucket_key TEXT NOT NULL, reason TEXT NOT NULL,
 created_at INTEGER NOT NULL, PRIMARY KEY(installation_id, bucket_key, reason)
);
CREATE TABLE revocations (
 installation_id TEXT NOT NULL, contribution_id TEXT NOT NULL,
 revoked_at INTEGER NOT NULL, epoch INTEGER NOT NULL,
 PRIMARY KEY(installation_id, contribution_id)
);
CREATE TABLE analysis_receipts (
 installation_id TEXT NOT NULL, analysis_id TEXT NOT NULL, request_id TEXT NOT NULL,
 received_at INTEGER NOT NULL, PRIMARY KEY(installation_id, analysis_id)
);
CREATE TABLE epoch_guards (
 operation_id TEXT PRIMARY KEY, installation_id TEXT NOT NULL,
 expected_epoch INTEGER NOT NULL, created_at INTEGER NOT NULL
);
