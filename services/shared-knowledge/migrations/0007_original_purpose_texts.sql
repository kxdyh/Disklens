-- Keep both original purpose texts bounded. Existing evidence stays category-only.
-- No retention job, extra table or per-request model call is introduced.
ALTER TABLE contributions ADD COLUMN short_purpose TEXT NOT NULL DEFAULT ''
 CHECK(length(short_purpose)<=12);
ALTER TABLE contributions ADD COLUMN reason TEXT NOT NULL DEFAULT ''
 CHECK(length(reason)<=30);
