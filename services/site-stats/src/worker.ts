import { collectSnapshot, SNAPSHOT_KEY, snapshotCacheControl } from "./snapshot.ts";
import type { Snapshot, StatsDatabase } from "./snapshot.ts";

interface StatsBucket {
  put(key: string, value: string, options: { httpMetadata: { contentType: string; cacheControl: string } }): Promise<unknown>;
}
export interface Env { DB: StatsDatabase; ASSETS: StatsBucket }

export async function refreshSnapshot(env: Env, now = Date.now()): Promise<Snapshot> {
  const snapshot = await collectSnapshot(env.DB, now);
  // A failed/incomplete scan never overwrites the last complete object.
  await env.ASSETS.put(SNAPSHOT_KEY, JSON.stringify(snapshot), {
    httpMetadata: { contentType: "application/json; charset=utf-8", cacheControl: snapshotCacheControl(snapshot) },
  });
  return snapshot;
}

export default {
  // No public database or refresh endpoint. Only the Cloudflare Cron Trigger runs this worker.
  async fetch(): Promise<Response> {
    return new Response("Not found", { status: 404, headers: { "Cache-Control": "no-store" } });
  },
  async scheduled(_event: unknown, env: Env): Promise<void> {
    await refreshSnapshot(env);
  },
};
