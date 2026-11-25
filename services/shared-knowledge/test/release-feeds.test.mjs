import test from 'node:test';
import assert from 'node:assert/strict';
import { handleReleaseFeeds } from '../src/release-feeds.ts';
import { database } from './sqlite-adapter.mjs';

const id = '11111111-1111-4111-8111-111111111111';
function fixture(type = 'installer') {
  const DB = database(), now = Date.UTC(2026, 10, 24);
  const filename = type === 'installer' ? 'DiskLens-Setup-1.1.3-x64.exe' : 'DiskLens-1.1.3-win-x64.zip';
  DB.sqlite.prepare(`INSERT INTO releases(id,version,channel,package_type,notes_json,created_at,updated_at,published_at,
    available,package_status,filename,artifact_key,bytes,sha256) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?)`)
    .run(id, '1.1.3', 'stable', type, '["回收修复"]', now, now, now, 1, 'ready', filename,
      `releases/${id}/${filename}`, 1234, 'a'.repeat(64));
  DB.sqlite.prepare('UPDATE release_state SET latest_id=? WHERE id=1').run(id);
  return { DB, async call(path, method = 'GET') {
    const response = await handleReleaseFeeds(new Request(`https://disklens.site${path}`, { method }), { DB });
    return { status: response.status, headers: response.headers, body: await response.json() };
  } };
}

test('dual manifest retains the installer as primary and adds the same-version portable artifact', async () => {
  const f = fixture();
  f.DB.sqlite.prepare(`UPDATE releases SET portable_status='ready',portable_filename=?,portable_artifact_key=?,portable_bytes=?,portable_sha256=? WHERE id=?`)
    .run('DiskLens-1.1.3-win-x64.zip', `releases/${id}/portable/DiskLens-1.1.3-win-x64.zip`, 2345, 'b'.repeat(64), id);
  for (const path of ['/downloads/manifest.json', '/v1/releases/manifest.json']) {
    const { status, body } = await f.call(path);
    assert.equal(status, 200); assert.equal(body.version, '1.1.3'); assert.equal(body.packageType, 'installer');
    assert.equal(body.filename, body.installer.filename); assert.equal(body.url, body.installer.url);
    assert.equal(body.bytes, 1234); assert.equal(body.sha256, 'a'.repeat(64));
    assert.equal(body.portable.filename, 'DiskLens-1.1.3-win-x64.zip');
    assert.equal(body.portable.bytes, 2345); assert.equal(body.portable.sha256, 'b'.repeat(64));
    assert.equal(body.portable.url, body.portable.path);
    assert.equal(body.portable.url, `https://assets.disklens.site/releases/${id}/portable/DiskLens-1.1.3-win-x64.zip`);
    assert.ok(!JSON.stringify(body).includes('artifact_owned'));
  }
  for (const path of ['/releases/history.json', '/v1/releases/history.json']) {
    const { body } = await f.call(path), release = body.releases.find(row => row.version === '1.1.3');
    assert.match(release.installer, /\.exe$/); assert.match(release.portable, /\.zip$/);
    assert.equal(release.available, true);
  }
});

test('an incomplete portable upload is never advertised and legacy installer remains usable', async () => {
  const f = fixture();
  f.DB.sqlite.prepare(`UPDATE releases SET portable_status='uploading',portable_filename='unverified.zip',portable_bytes=100,portable_artifact_key='releases/unverified.zip' WHERE id=?`).run(id);
  const { body } = await f.call('/downloads/manifest.json');
  assert.ok(body.installer); assert.equal(body.portable, undefined);
  const history = (await f.call('/releases/history.json')).body.releases.find(row => row.version === '1.1.3');
  assert.ok(history.installer); assert.equal(history.portable, undefined);
});

test('legacy portable-only releases keep their primary ZIP fields', async () => {
  const f = fixture('portable'), { body } = await f.call('/downloads/manifest.json');
  assert.equal(body.packageType, 'portable'); assert.equal(body.filename, body.portable.filename);
  assert.equal(body.bytes, body.portable.bytes); assert.equal(body.installer, undefined);
});

test('withdrawn releases expose neither download and missing latest returns unavailable', async () => {
  const f = fixture();
  f.DB.sqlite.prepare(`UPDATE releases SET available=0,portable_status='ready',portable_filename='private.zip',portable_artifact_key='releases/private.zip',portable_bytes=100 WHERE id=?`).run(id);
  assert.equal((await f.call('/downloads/manifest.json')).status, 503);
  const release = (await f.call('/releases/history.json')).body.releases.find(row => row.version === '1.1.3');
  assert.equal(release.installer, undefined); assert.equal(release.portable, undefined); assert.equal(release.available, false);
});

test('old latest-update contract and methods remain unchanged', async () => {
  const f = fixture();
  for (const path of ['/releases/latest.json', '/v1/releases/latest.json']) {
    const { status, body, headers } = await f.call(path);
    assert.equal(status, 200); assert.equal(body.version, '1.1.3'); assert.equal(body.schemaVersion, 1);
    assert.equal(body.downloadPage, 'https://disklens.site/#download'); assert.equal(body.publishedAt, '2026-11-24');
    assert.equal(headers.get('cache-control'), 'no-store');
  }
  assert.equal((await f.call('/downloads/manifest.json', 'POST')).status, 405);
});
