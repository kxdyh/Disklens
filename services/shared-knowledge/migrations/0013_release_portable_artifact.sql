-- A second public artifact belongs to the same version; installer metadata is retained.
ALTER TABLE releases ADD COLUMN portable_status TEXT NOT NULL DEFAULT 'empty' CHECK(portable_status IN ('empty','uploading','ready'));
ALTER TABLE releases ADD COLUMN portable_filename TEXT NOT NULL DEFAULT '';
ALTER TABLE releases ADD COLUMN portable_artifact_key TEXT NOT NULL DEFAULT '';
ALTER TABLE releases ADD COLUMN portable_artifact_owned INTEGER NOT NULL DEFAULT 1 CHECK(portable_artifact_owned IN (0,1));
ALTER TABLE releases ADD COLUMN portable_bytes INTEGER NOT NULL DEFAULT 0;
ALTER TABLE releases ADD COLUMN portable_sha256 TEXT NOT NULL DEFAULT '';
