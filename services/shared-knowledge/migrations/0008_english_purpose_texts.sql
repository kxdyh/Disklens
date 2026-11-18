-- English translations of the existing original Chinese purpose pair.
-- Translation does not renew evidence timestamps, change authors or create votes.
ALTER TABLE contributions ADD COLUMN short_purpose_en TEXT NOT NULL DEFAULT ''
 CHECK(length(short_purpose_en)<=80);
ALTER TABLE contributions ADD COLUMN reason_en TEXT NOT NULL DEFAULT ''
 CHECK(length(reason_en)<=240);
