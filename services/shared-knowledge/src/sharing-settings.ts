import type { Database } from "./worker.ts";

type SettingsRow = { allow_new_contributors: number; registration_limit: number; revision: number; updated_at: number | null };
export async function readOperatorSettings(env: { DB: Database }): Promise<SettingsRow> {
  const row = await env.DB.prepare("SELECT allow_new_contributors,registration_limit,revision,updated_at FROM operator_settings WHERE id=1").first<SettingsRow>();
  if (!row) throw new Error("sharing_settings_missing");
  return row;
}
