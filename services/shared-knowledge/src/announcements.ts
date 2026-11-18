import type { Database } from "./worker.ts";

/** Public, plain-text announcements. Editing is provided by a separate private service. */
export async function handleAnnouncements(request: Request, env: { DB: Database }): Promise<Response> {
  const json = (value: unknown, status = 200) => new Response(JSON.stringify(value), { status,
    headers: { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff" } });
  if (request.method !== "GET") return json({ error: "method_not_allowed" }, 405);
  if (new URL(request.url).search) return json({ error: "unexpected_fields" }, 400);
  try {
    const result = await env.DB.prepare(`SELECT id,title_zh,body_zh,title_en,body_en,priority,revision,published_at,updated_at
      FROM announcements WHERE published=1 ORDER BY priority DESC,published_at DESC,id ASC LIMIT 51`).all<{
      id: string; title_zh: string; body_zh: string; title_en: string; body_en: string; priority: number;
      revision: number; published_at: number; updated_at: number }>();
    if (result.results.length > 50) return json({ error: "announcement_list_too_large" }, 503);
    return json({ schemaVersion: 1, items: result.results.map(row => ({ id: row.id, titleZh: row.title_zh,
      bodyZh: row.body_zh, titleEn: row.title_en, bodyEn: row.body_en, priority: row.priority, revision: row.revision,
      publishedAt: new Date(row.published_at).toISOString(), updatedAt: new Date(row.updated_at).toISOString() })) });
  } catch { return json({ error: "announcements_temporarily_unavailable" }, 503); }
}
