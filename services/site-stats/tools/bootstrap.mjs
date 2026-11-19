import { execFileSync } from 'node:child_process';
import { writeFileSync, mkdirSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { collectSnapshot, SNAPSHOT_KEY, snapshotCacheControl } from '../src/snapshot.ts';

// Explicit one-off release operation. Reads D1; writes R2 only with --upload.
// Authentication remains in Wrangler's existing local auth profile/environment.
const args = process.argv.slice(2);
const option = name => { const index = args.indexOf(name); return index >= 0 ? args[index + 1] : undefined; };
const wrangler = option('--wrangler');
const config = option('--config');
const output = option('--out');
const bucket = option('--bucket');
if (!wrangler || !config || !output || (args.includes('--upload') && !bucket)) {
  console.error('Usage: node tools/bootstrap.mjs --wrangler <wrangler.js> --config <config> --out <snapshot.json> [--upload --bucket <bucket>]');
  process.exit(2);
}
const cwd = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const readMetrics = [];
function command(argv) {
  return execFileSync(process.execPath, [resolve(wrangler), ...argv], {
    cwd, encoding: 'utf8', timeout: 120000, maxBuffer: 32 * 1024 * 1024,
    env: { ...process.env, WRANGLER_SEND_METRICS: 'false', CI: 'true' },
  });
}
function literal(value) {
  if (typeof value === 'number' && Number.isSafeInteger(value)) return String(value);
  if (typeof value === 'string') return `'${value.replaceAll("'", "''")}'`;
  throw new Error('unsupported_sql_parameter');
}
class Statement {
  constructor(sql, values = []) { this.sql = sql; this.values = values; }
  bind(...values) { return new Statement(this.sql, values); }
  rows() {
    let index = 0;
    const sql = this.sql.replaceAll('?', () => literal(this.values[index++]));
    if (index !== this.values.length) throw new Error('sql_parameter_count');
    const result = JSON.parse(command(['d1', 'execute', 'DB', '--config', resolve(config), '--remote', '--command', sql, '--json']));
    if (!Array.isArray(result) || result.length !== 1 || result[0].success === false || !Array.isArray(result[0].results))
      throw new Error('database_read_failed');
    const meta = result[0].meta ?? {};
    readMetrics.push({ rowsRead: meta.rows_read ?? 0, rowsWritten: meta.rows_written ?? 0,
      sqlDurationMs: meta.timings?.sql_duration_ms ?? meta.duration ?? 0 });
    return result[0].results;
  }
  async first() { return this.rows()[0] ?? null; }
  async all() { return { results: this.rows() }; }
}
const snapshot = await collectSnapshot({ prepare(sql) { return new Statement(sql); } });
const filename = resolve(output);
mkdirSync(dirname(filename), { recursive: true });
writeFileSync(filename, `${JSON.stringify(snapshot, null, 2)}\n`, 'utf8');
if (args.includes('--upload')) {
  command(['r2', 'object', 'put', `${bucket}/${SNAPSHOT_KEY}`, '--file', filename, '--remote',
    '--content-type', 'application/json; charset=utf-8', '--cache-control', snapshotCacheControl(snapshot)]);
}
console.log(JSON.stringify({ ...snapshot, uploaded: args.includes('--upload'), readMetrics }, null, 2));
