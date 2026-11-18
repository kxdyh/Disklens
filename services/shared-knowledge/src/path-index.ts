import { fingerprintKey, ruleFor, validFingerprint } from "./rules.ts";
import type { Fingerprint } from "./rules.ts";

type IndexStatement = {
  bind(...values: unknown[]): IndexStatement;
  all<T>(): Promise<{results: T[]}>;
};
type IndexDatabase = {prepare(sql: string): IndexStatement};
type IndexRow = {key:string;software_id:string;version_major:string;role_id:string;shape:string};

/** Exact, paged public identities only. Existing buckets remain the source of truth. */
export async function createSharedPathIndex(env: {DB: IndexDatabase}, url: URL, now: number) {
  const cursor = url.searchParams.get("cursor") ?? "";
  if (cursor.length > 512 || /[\u0000-\u001f]/.test(cursor))
    throw new Error("invalid_path_index_cursor");
  const rows = await env.DB.prepare(`SELECT b.key,b.software_id,b.version_major,b.role_id,b.shape FROM buckets b
    WHERE b.key>?
      AND EXISTS(SELECT 1 FROM contributions c JOIN installations i ON i.id=c.installation_id
        WHERE c.bucket_key=b.key AND c.withdrawn=0 AND c.epoch>i.revoked_through_epoch
          AND c.confidence_level IN ('Medium','High'))
    ORDER BY b.key LIMIT 1001`).bind(cursor).all<IndexRow>();
  const page = rows.results.slice(0,1000);
  const shapes = new Set<string>(), legacyKeys = new Set<string>();
  for (const row of page) {
    const fingerprint: Fingerprint = {softwareId:row.software_id,versionMajor:row.version_major,
      roleId:row.role_id,shape:row.shape};
    if (!validFingerprint(fingerprint) || ruleFor(fingerprint)?.reusable === false
      || fingerprintKey(fingerprint) !== row.key) continue;
    if (/^purpose3-shared-(directory|file)-[a-f0-9]{64}$/.test(row.shape)) shapes.add(row.shape);
    else {
      const digest = await crypto.subtle.digest("SHA-256",new TextEncoder().encode(row.key));
      legacyKeys.add([...new Uint8Array(digest)].map(value=>value.toString(16).padStart(2,"0")).join(""));
    }
  }
  // Cursor advances over scanned buckets, including filtered legacy observations.
  // The two exact sets together contain at most the 1000 rows in this page.
  return {version:1,shapes:[...shapes],legacyKeys:[...legacyKeys],complete:true,
    builtAt:new Date(now).toISOString(),expiresAt:new Date(now+60000).toISOString(),
    nextCursor:rows.results.length>1000 ? page[page.length-1].key : null};
}
