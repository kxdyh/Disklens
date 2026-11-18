import type { Database } from "./worker.ts";

type Release = { id: string; version: string; channel: string; build: string; package_type: string; notes_json: string;
  notes_en_json: string; published_at: number | null; available: number; filename: string; artifact_key: string; bytes: number; sha256: string };
const columns = "id,version,channel,build,package_type,notes_json,notes_en_json,published_at,available,filename,artifact_key,bytes,sha256";
const downloadUrl = (row: Release) => `https://assets.disklens.site/${row.artifact_key.split("/").map(encodeURIComponent).join("/")}`;
const date = (row: Release) => new Date(row.published_at!).toISOString().slice(0, 10);
const notes = (row: Release) => ({ notes: JSON.parse(row.notes_json) as string[],
  ...(row.notes_en_json !== "[]" ? { notesEn: JSON.parse(row.notes_en_json) as string[] } : {}) });

/** Keep the old website URLs returning 200 directly: installed applications disallow redirects. */
export async function handleReleaseFeeds(request: Request, env: { DB: Database }): Promise<Response> {
  const json = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status,
    headers: { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff" } });
  if (request.method !== "GET") return json({ error: "method_not_allowed" }, 405);
  const path = new URL(request.url).pathname;
  try {
    if (path === "/releases/history.json" || path === "/v1/releases/history.json") {
      const rows = await env.DB.prepare(`SELECT ${columns} FROM releases WHERE published_at IS NOT NULL ORDER BY published_at DESC,id LIMIT 100`).all<Release>();
      return json({ schemaVersion: 1, releases: rows.results.map(row => ({ version: row.version, date: date(row),
        channel: row.available ? row.channel : "withdrawn", available: !!row.available, ...notes(row),
        ...(row.available ? { [row.package_type]: downloadUrl(row) } : {}) })) });
    }
    const row = await env.DB.prepare(`SELECT ${columns.split(",").map(value => `r.${value}`).join(",")} FROM releases r
      JOIN release_state s ON s.latest_id=r.id WHERE s.id=1 AND r.available=1 AND r.package_status='ready'`).first<Release>();
    if (!row || row.published_at === null) return json({ error: "release_unavailable" }, 503);
    if (path === "/releases/latest.json" || path === "/v1/releases/latest.json") return json({ schemaVersion: 1,
      version: row.version, publishedAt: date(row), minimumSupportedVersion: "1.0.0", downloadPage: "https://disklens.site/#download", ...notes(row) });
    if (path === "/downloads/manifest.json" || path === "/v1/releases/manifest.json") {
      const artifact = { filename: row.filename, path: downloadUrl(row), url: downloadUrl(row), bytes: row.bytes, sha256: row.sha256 };
      return json({ version: row.version, channel: row.channel === "stable" ? "正式版" : "预览版", platform: "Windows x64", date: date(row),
        build: row.build, ...artifact, packageType: row.package_type, [row.package_type]: artifact });
    }
    return json({ error: "not_found" }, 404);
  } catch { return json({ error: "releases_temporarily_unavailable" }, 503); }
}
