import test from 'node:test';
import assert from 'node:assert/strict';
import { DatabaseSync } from 'node:sqlite';
import { readFileSync } from 'node:fs';
import { collectSnapshot, snapshotWindow, snapshotCacheControl } from '../src/snapshot.ts';
import worker, { refreshSnapshot } from '../src/worker.ts';

const now = Date.parse('2026-11-19T02:20:00Z');
const dayStart = Date.parse('2026-11-18T16:00:00Z');
function database() {
  const sqlite = new DatabaseSync(':memory:');
  sqlite.exec(`CREATE TABLE installations(id TEXT PRIMARY KEY,revoked_through_epoch INTEGER,enabled INTEGER);
    CREATE TABLE buckets(key TEXT PRIMARY KEY,software_id TEXT,version_major TEXT,role_id TEXT,shape TEXT);
    CREATE TABLE contributions(id TEXT PRIMARY KEY,bucket_key TEXT,installation_id TEXT,withdrawn INTEGER,epoch INTEGER,confidence_level TEXT);
    CREATE INDEX evidence ON contributions(bucket_key,withdrawn);
    CREATE TABLE analysis_receipts(installation_id TEXT,analysis_id TEXT,received_at INTEGER,PRIMARY KEY(installation_id,analysis_id));`);
  const pages = [];
  class Statement {
    constructor(sql, values = []) { this.sql = sql; this.values = values; }
    bind(...values) { return new Statement(this.sql, values); }
    async first() { return sqlite.prepare(this.sql).get(...this.values) ?? null; }
    async all() { const results = sqlite.prepare(this.sql).all(...this.values); pages.push(results.length); return { results }; }
  }
  return { sqlite, pages, prepare(sql) { return new Statement(sql); } };
}
const shared = number => ({ softwareId: 'generic-v3', versionMajor: '0', roleId: 'path3:os-temp/cache',
  shape: `purpose3-shared-directory-${number.toString(16).padStart(64, '0')}` });
function addBucket(db, fp, { id = 'author', floor = -1, epoch = 1, withdrawn = 0, confidence = 'High', key } = {}) {
  key ??= `${fp.softwareId}|${fp.versionMajor}|${fp.roleId}|${fp.shape}`;
  db.sqlite.prepare('INSERT OR IGNORE INTO installations VALUES(?,?,0)').run(id, floor);
  db.sqlite.prepare('INSERT OR IGNORE INTO buckets VALUES(?,?,?,?,?)').run(key, fp.softwareId, fp.versionMajor, fp.roleId, fp.shape);
  db.sqlite.prepare('INSERT INTO contributions VALUES(?,?,?,?,?,?)').run(`${id}:${key}`, key, id, withdrawn, epoch, confidence);
}

test('China midnight and noon define non-overlapping twelve-hour refreshes', () => {
  assert.deepEqual(snapshotWindow(now), { day: '2026-11-19', dayStart, expiresAt: Date.parse('2026-11-19T04:00:00Z') });
  assert.equal(snapshotWindow(Date.parse('2026-11-19T03:59:59.999Z')).expiresAt, Date.parse('2026-11-19T04:00:00Z'));
  assert.equal(snapshotWindow(Date.parse('2026-11-19T04:00:00Z')).expiresAt, Date.parse('2026-11-19T16:00:00Z'));
  assert.equal(snapshotWindow(Date.parse('2026-11-19T16:00:00Z')).day, '2026-11-20');
  assert.equal(snapshotWindow(Date.parse('2026-11-19T15:59:59Z')).day, '2026-11-19');
});

test('accepted receipt totals are unique, day-bounded and independent of later withdrawal', async () => {
  const db = database();
  const add = db.sqlite.prepare('INSERT OR IGNORE INTO analysis_receipts VALUES(?,?,?)');
  add.run('a', 'today-start', dayStart);
  add.run('a', 'today-start', dayStart + 50);
  add.run('a', 'later-withdrawn', now - 500);
  add.run('a', 'private-observation', now - 10);
  add.run('a', 'snapshot-instant', now);
  add.run('a', 'yesterday', dayStart - 1);
  add.run('a', 'future', now + 1);
  const result = await collectSnapshot(db, now);
  assert.equal(result.todayContributions, 4);
  assert.equal(result.totalExperiences, 0);
  assert.equal(result.day, '2026-11-19');
  assert.deepEqual(Object.keys(result).sort(), ['schemaVersion', 'day', 'timeZone', 'todayContributions', 'totalExperiences', 'updatedAt', 'expiresAt', 'refreshIntervalSeconds'].sort());
  assert.equal(result.timeZone, 'Asia/Shanghai');
  assert.equal(result.refreshIntervalSeconds, 43200);
});

test('total uses the complete public index boundary, never author counts or private observations', async () => {
  const db = database();
  addBucket(db, shared(1));
  addBucket(db, shared(1), { id: 'second-author', confidence: 'Medium' });
  addBucket(db, { softwareId: 'android-sdk', versionMajor: '0', roleId: 'sdk-temp', shape: 'cache-only-v1' });
  addBucket(db, { ...shared(2), shape: `purpose3-observation-directory-${'a'.repeat(32)}` });
  addBucket(db, shared(3), { confidence: 'Low' });
  addBucket(db, shared(4), { id: 'revoked-author', floor: 1, epoch: 1 });
  addBucket(db, shared(5), { withdrawn: 1 });
  addBucket(db, shared(6), { key: 'inconsistent-bucket-key' });
  addBucket(db, shared(7), { confidence: 'Unknown' });
  addBucket(db, { ...shared(8), versionMajor: 'invalid' });
  // Disabled consent does not withdraw history. The path index checks the epoch barrier, not enabled.
  addBucket(db, shared(9), { id: 'disabled-author', floor: 1, epoch: 2 });
  const result = await collectSnapshot(db, now);
  assert.equal(result.totalExperiences, 3);
});

test('keyset pagination scans all eligible identities beyond the first thousand', async () => {
  const db = database();
  for (let index = 1; index <= 1407; index++) addBucket(db, shared(index));
  const result = await collectSnapshot(db, now);
  assert.equal(result.totalExperiences, 1407);
  assert.deepEqual(db.pages, [1000, 407]);
});

test('receipt time index enables a covering range query without changing stored rows', () => {
  const db = database();
  db.sqlite.exec(readFileSync(new URL('../migrations/0001_receipt_time_index.sql', import.meta.url), 'utf8'));
  const plan = db.sqlite.prepare('EXPLAIN QUERY PLAN SELECT COUNT(*) FROM analysis_receipts WHERE received_at>=? AND received_at<?').all(dayStart, now);
  assert.match(plan.map(row => row.detail).join(' '), /COVERING INDEX analysis_receipts_received_at/);
});

test('only a completed snapshot is published, with safe JSON cache metadata', async () => {
  const db = database(), writes = [];
  const snapshot = await refreshSnapshot({ DB: db, ASSETS: { async put(...args) { writes.push(args); } } }, now);
  assert.equal(writes.length, 1);
  assert.equal(writes[0][0], 'stats/contributions.json');
  assert.deepEqual(JSON.parse(writes[0][1]), snapshot);
  assert.equal(writes[0][2].httpMetadata.contentType, 'application/json; charset=utf-8');
  assert.equal(snapshotCacheControl(snapshot), 'public, max-age=0, must-revalidate');
  await assert.rejects(refreshSnapshot({ DB: { prepare() { throw new Error('unavailable'); } }, ASSETS: { async put() { writes.push('bad'); } } }, now));
  assert.equal(writes.length, 1);
});

test('no public route can trigger a database scan or refresh', async () => {
  const response = await worker.fetch(new Request('https://unused.invalid/refresh'), { DB: null, ASSETS: null });
  assert.equal(response.status, 404);
  assert.equal(response.headers.get('Cache-Control'), 'no-store');
});
