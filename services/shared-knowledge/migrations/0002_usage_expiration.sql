ALTER TABLE usage_limits ADD COLUMN day TEXT NOT NULL DEFAULT '';
CREATE INDEX usage_limits_day ON usage_limits(day);
