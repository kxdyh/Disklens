-- Public release metadata; operator implementation lives outside the public repository.
CREATE TABLE releases (
  id TEXT PRIMARY KEY NOT NULL,
  version TEXT NOT NULL UNIQUE,
  channel TEXT NOT NULL CHECK(channel IN ('preview','stable')),
  build TEXT NOT NULL DEFAULT '',
  package_type TEXT NOT NULL CHECK(package_type IN ('installer','portable')),
  notes_json TEXT NOT NULL,
  notes_en_json TEXT NOT NULL DEFAULT '[]',
  revision INTEGER NOT NULL DEFAULT 1,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL,
  published_at INTEGER,
  available INTEGER NOT NULL DEFAULT 0 CHECK(available IN (0,1)),
  package_status TEXT NOT NULL DEFAULT 'empty' CHECK(package_status IN ('empty','uploading','ready')),
  filename TEXT NOT NULL DEFAULT '',
  artifact_key TEXT NOT NULL DEFAULT '',
  artifact_owned INTEGER NOT NULL DEFAULT 1 CHECK(artifact_owned IN (0,1)),
  bytes INTEGER NOT NULL DEFAULT 0,
  sha256 TEXT NOT NULL DEFAULT ''
);
CREATE TABLE release_state (
  id INTEGER PRIMARY KEY CHECK(id=1),
  latest_id TEXT,
  revision INTEGER NOT NULL DEFAULT 1
);
INSERT INTO release_state(id) VALUES(1);
CREATE INDEX releases_public_order ON releases(available,published_at DESC,version);
