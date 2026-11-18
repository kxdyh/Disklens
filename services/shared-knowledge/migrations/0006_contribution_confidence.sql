-- Never infer confidence for historical rows. Missing grades stay Unknown.
ALTER TABLE contributions ADD COLUMN confidence_level TEXT NOT NULL DEFAULT 'Unknown'
 CHECK(confidence_level IN ('High','Medium','Low','Unknown'));
