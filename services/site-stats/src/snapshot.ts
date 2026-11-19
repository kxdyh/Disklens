import { fingerprintKey, ruleFor, validFingerprint } from "../../shared-knowledge/src/rules.ts";

interface Statement {
  bind(...values: unknown[]): Statement;
  first<T>(): Promise<T | null>;
  all<T>(): Promise<{ results: T[] }>;
}
export interface StatsDatabase { prepare(sql: string): Statement }
export type BucketRow = { key: string; software_id: string; version_major: string; role_id: string; shape: string };
export type Snapshot = {
  schemaVersion: 1; day: string; timeZone: "Asia/Shanghai";
  todayContributions: number; totalExperiences: number;
  updatedAt: string; expiresAt: string; refreshIntervalSeconds: 43200;
};

export const SNAPSHOT_KEY = "stats/contributions.json";
export const PAGE_SIZE = 1000;
export const RECEIPT_COUNT_SQL = `SELECT COUNT(*) AS count FROM analysis_receipts
  WHERE received_at>=? AND received_at<?`;
// Identical qualification boundary to shared-knowledge/src/path-index.ts.
// EXISTS keeps multiple authors of one public identity from inflating the total.
export const BUCKET_PAGE_SQL = `SELECT b.key,b.software_id,b.version_major,b.role_id,b.shape FROM buckets b
  WHERE b.key>?
    AND EXISTS(SELECT 1 FROM contributions c JOIN installations i ON i.id=c.installation_id
      WHERE c.bucket_key=b.key AND c.withdrawn=0 AND c.epoch>i.revoked_through_epoch
        AND c.confidence_level IN ('Medium','High'))
  ORDER BY b.key LIMIT ?`;

/** A shared snapshot refreshes at 00:00 and 12:00 China time, never after midnight under yesterday's label. */
export function snapshotWindow(now: number) {
  if (!Number.isSafeInteger(now) || now < 0) throw new Error("invalid_snapshot_time");
  const hour = 3600000, shifted = now + 8 * hour;
  return {
    day: new Date(shifted).toISOString().slice(0, 10),
    dayStart: Math.floor(shifted / (24 * hour)) * 24 * hour - 8 * hour,
    expiresAt: (Math.floor(shifted / (12 * hour)) + 1) * 12 * hour - 8 * hour,
  };
}

export function reusableBucket(row: BucketRow): boolean {
  const fingerprint = { softwareId: row.software_id, versionMajor: row.version_major,
    roleId: row.role_id, shape: row.shape };
  return validFingerprint(fingerprint) && ruleFor(fingerprint)?.reusable !== false
    && fingerprintKey(fingerprint) === row.key;
}

export async function collectSnapshot(db: StatsDatabase, now = Date.now()): Promise<Snapshot> {
  const window = snapshotWindow(now);
  // Permanent original-analysis receipts represent accepted Medium/High uploads,
  // including later withdrawals. A replay has the same PK and is never a new receipt.
  const receipt = await db.prepare(RECEIPT_COUNT_SQL).bind(window.dayStart, now + 1).first<{ count: number }>();
  if (!receipt || !Number.isSafeInteger(receipt.count) || receipt.count < 0)
    throw new Error("invalid_contribution_count");
  let cursor = "", total = 0;
  for (;;) {
    const { results } = await db.prepare(BUCKET_PAGE_SQL).bind(cursor, PAGE_SIZE).all<BucketRow>();
    for (const row of results) {
      if (row.key <= cursor) throw new Error("invalid_bucket_cursor");
      cursor = row.key;
      if (reusableBucket(row)) total++;
    }
    if (results.length < PAGE_SIZE) break;
  }
  if (!Number.isSafeInteger(total)) throw new Error("invalid_experience_count");
  return { schemaVersion: 1, day: window.day, timeZone: "Asia/Shanghai",
    todayContributions: receipt.count, totalExperiences: total,
    updatedAt: new Date(now).toISOString(), expiresAt: new Date(window.expiresAt).toISOString(),
    refreshIntervalSeconds: 43200 };
}

/** Revalidation is cheap (R2 only); never cache an older day for another twelve hours. */
export function snapshotCacheControl(snapshot: Snapshot): string {
  return `public, max-age=0, must-revalidate`;
}
