-- Optional additive covering index for the twice-daily range COUNT.
-- No existing record is changed, removed, expired, or reclassified.
CREATE INDEX IF NOT EXISTS analysis_receipts_received_at ON analysis_receipts(received_at);
