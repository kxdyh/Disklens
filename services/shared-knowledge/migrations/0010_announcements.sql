CREATE TABLE IF NOT EXISTS announcements (
  id TEXT PRIMARY KEY NOT NULL,
  title_zh TEXT NOT NULL,
  body_zh TEXT NOT NULL,
  title_en TEXT NOT NULL DEFAULT '',
  body_en TEXT NOT NULL DEFAULT '',
  published INTEGER NOT NULL DEFAULT 0 CHECK (published IN (0, 1)),
  priority INTEGER NOT NULL DEFAULT 0,
  revision INTEGER NOT NULL DEFAULT 1,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL,
  published_at INTEGER
);

CREATE INDEX IF NOT EXISTS announcements_public_order
  ON announcements(published, priority DESC, published_at DESC, updated_at DESC);

CREATE TABLE IF NOT EXISTS operator_settings (
  id INTEGER PRIMARY KEY CHECK (id=1),
  allow_new_contributors INTEGER NOT NULL DEFAULT 1 CHECK (allow_new_contributors IN (0,1)),
  registration_limit INTEGER NOT NULL DEFAULT 10 CHECK (registration_limit BETWEEN 1 AND 120),
  revision INTEGER NOT NULL DEFAULT 1,
  updated_at INTEGER
);
INSERT OR IGNORE INTO operator_settings(id) VALUES(1);

-- Short-lived counter values reuse bounded slots; business experience is never deleted.
CREATE TABLE IF NOT EXISTS registration_windows (
  slot INTEGER PRIMARY KEY CHECK (slot BETWEEN 0 AND 65535),
  window INTEGER NOT NULL,
  count INTEGER NOT NULL
);

ALTER TABLE installations ADD COLUMN joined_contribution_plan INTEGER NOT NULL DEFAULT 0
  CHECK (joined_contribution_plan IN (0,1));
UPDATE installations SET joined_contribution_plan=1 WHERE enabled=1 OR agreement_version<>'';
