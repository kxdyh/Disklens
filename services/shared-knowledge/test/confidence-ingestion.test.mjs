import test from 'node:test';
import assert from 'node:assert/strict';
import {handle} from '../src/worker.ts';
import {AGREEMENT_VERSION} from '../src/rules.ts';
import {database} from './sqlite-adapter.mjs';

const network='203.0.113.9';
const example=(index,confidenceLevel='High')=>({softwareId:'wegame',versionMajor:String(index),
  roleId:'wegame-temp',shape:'cache-only-v1',analysisId:`confidence-analysis-${index}`,purpose:'cache',
  action:'Review',source:'fresh-ai',providerClass:'official',modelFamily:'deepseek',
  defaultPrompt:true,sharedContext:false,confidenceLevel});
const missingGrade=index=>{const {confidenceLevel,...item}=example(index);return item;};
const mixed=()=>[example(1,'Low'),example(2,'Medium'),example(3,'Unknown'),example(4,'High'),missingGrade(5)];
function fixture() {
  const env={DB:database(),SERVICE_MODE:'enabled'};
  async function call(path,method='GET',payload,who) {
    const response=await handle(new Request(`http://localhost${path}`,{method,headers:{
      'content-type':'application/json','CF-Connecting-IP':network,
      ...(who?{'X-Installation-Id':who.installationId,Authorization:`Bearer ${who.managementToken}`}:{})},
      body:payload===undefined?undefined:JSON.stringify(payload)}),env);
    return {status:response.status,body:await response.json(),headers:response.headers};
  }
  async function install() {
    const registered=await call('/v1/install','POST',{});assert.equal(registered.status,201);
    const who=registered.body;
    const consent=await call('/v1/consent','PUT',{expectedEpoch:0,enabled:true,
      agreementVersion:AGREEMENT_VERSION,shareHistory:true,policyRevision:'confidence-gate'},who);
    assert.equal(consent.status,200);who.epoch=consent.body.epoch;return who;
  }
  const post=(who,items)=>call('/v1/contributions','POST',{epoch:who.epoch,items},who);
  const businessState=()=>['buckets','contributions','analysis_receipts'].map(table=>
    env.DB.sqlite.prepare(`SELECT * FROM ${table} ORDER BY 1`).all());
  return {env,call,install,post,businessState};
}
function assertReceipt(result,accepted,skipped,epoch=1) {
  assert.equal(result.status,200);
  assert.equal(result.body.epoch,epoch);
  assert.equal(result.body.accepted,accepted);
  assert.equal(result.body.skipped,skipped);
}

test('direct API low/unknown/missing grades never prepare business writes',async()=>{
  const f=fixture(),who=await f.install();
  const originalPrepare=f.env.DB.prepare.bind(f.env.DB),prepared=[];
  f.env.DB.prepare=sql=>{prepared.push(sql);return originalPrepare(sql);};
  const protocols=[{},
    {softwareId:'generic-v2',versionMajor:'0',roleId:'path2:user-local/cache',shape:`tree2-${'a'.repeat(64)}`},
    {softwareId:'generic-v3',versionMajor:'0',roleId:'path3:os-local/cache',shape:'purpose3-directory'}];
  const initialCapacity=f.env.DB.sqlite.prepare("SELECT name,used FROM resource_caps WHERE name IN ('buckets','contributions','analysis_receipts') ORDER BY name").all();
  for(const protocol of protocols) for(const confidenceLevel of ['Low','Unknown',undefined]) {
    const item={...(confidenceLevel===undefined?missingGrade(1):example(1,confidenceLevel)),...protocol};
    assertReceipt(await f.post(who,[item]),0,1);
  }
  assert.deepEqual(f.businessState(),[[],[],[]]);
  assert.equal(prepared.some(sql=>/^(INSERT INTO|UPDATE) (buckets|contributions|analysis_receipts)\b/.test(sql)),false);
  assert.deepEqual(f.env.DB.sqlite.prepare("SELECT name,used FROM resource_caps WHERE name IN ('buckets','contributions','analysis_receipts') ORDER BY name").all(),initialCapacity);
  // A filtered upload does not consume the original analysis identifier.
  assertReceipt(await f.post(who,[example(1,'Medium')]),1,0);
  assertReceipt(await f.post(who,[example(2,'Low')]),0,1);
  assertReceipt(await f.post(who,[example(2,'High')]),1,0);
});

test('mixed upload retains only Medium/High without receipts for filtered items',async()=>{
  const f=fixture(),who=await f.install(),items=mixed();
  assertReceipt(await f.post(who,items),2,3);
  const [buckets,rows,receipts]=f.businessState();
  assert.equal(buckets.length,2);assert.equal(receipts.length,2);
  assert.deepEqual(rows.map(row=>row.confidence_level).sort(),['High','Medium']);
  assert.deepEqual(receipts.map(row=>row.analysis_id).sort(),['confidence-analysis-2','confidence-analysis-4']);
  assertReceipt(await f.post(who,[items[0],items[2],items[4]]),0,3);
  assert.equal(f.env.DB.sqlite.prepare('SELECT COUNT(*) AS n FROM buckets').get().n,2);
});

test('mixed retries preserve full receipt counts and low payloads cannot overwrite or revise accepted rows',async()=>{
  const f=fixture(),who=await f.install(),items=mixed();
  assertReceipt(await f.post(who,items),2,3);const before=f.businessState();
  assertReceipt(await f.post(who,items.toReversed()),0,5);
  assert.deepEqual(f.businessState(),before);
  assertReceipt(await f.post(who,[{...example(2,'Low'),analysisId:'attempted-low-overwrite',action:'Keep'}]),0,1);
  assert.deepEqual(f.businessState(),before);
  assertReceipt(await f.post(who,[{...example(2,'High'),analysisId:'confidence-analysis-4'}]),0,1);
  assert.deepEqual(f.businessState(),before,'existing analysis receipts still prevent replay into another bucket');
  const withdrawn=await f.call('/v1/contributions?epoch=1','DELETE',undefined,who);
  assert.equal(withdrawn.status,200);assert.equal(withdrawn.body.withdrawn,2);who.epoch=withdrawn.body.epoch;
  const replay=await f.post(who,items);assert.equal(replay.status,200);
  assertReceipt(replay,0,5,2);
  assert.equal(f.env.DB.sqlite.prepare('SELECT COUNT(*) AS n FROM contributions').get().n,0);
  assert.equal(f.env.DB.sqlite.prepare('SELECT COUNT(*) AS n FROM analysis_receipts').get().n,2);
});

test('duplicate analysis IDs and fingerprints are rejected across the full unfiltered batch',async()=>{
  const f=fixture(),who=await f.install();
  const cases=[
    [example(1),{...example(2,'Low'),analysisId:example(1).analysisId}],
    [example(1,'Low'),{...example(2),analysisId:example(1).analysisId}],
    [example(1),{...example(1,'Unknown'),analysisId:'another-analysis-id'}],
    [example(1,'Low'),{...missingGrade(1),analysisId:'another-analysis-id'}]
  ];
  for(const items of cases) {
    const result=await f.post(who,items);assert.equal(result.status,400);
    assert.equal(result.body.error,'duplicate_batch_items');
    assert.deepEqual(f.businessState(),[[],[],[]]);
  }
});

test('low grades do not hide malformed enums, missing required fields, body size or original batch limits',async()=>{
  const f=fixture(),who=await f.install();
  const {modelFamily,...missingRequired}=example(2,'Low');
  for(const invalid of [example(2,'VeryHigh'),example(2,null),example(2,42),
    {...example(2,'Low'),purpose:'not-a-purpose'},missingRequired]) {
    const result=await f.post(who,[example(1),invalid]);assert.equal(result.status,400);
    assert.deepEqual(f.businessState(),[[],[],[]]);
  }
  const overLimit=await f.post(who,Array.from({length:11},(_,n)=>example(n,'Low')));
  assert.equal(overLimit.status,400);assert.equal(overLimit.body.error,'batch_limit_10');
  const oversized=await f.post(who,[{...example(1,'Low'),reason:'x'.repeat(17000)}]);
  assert.equal(oversized.status,413);assert.deepEqual(f.businessState(),[[],[],[]]);
});

test('all-filtered uploads recheck consent and cannot return a successful stale receipt',async()=>{
  const f=fixture(),who=await f.install(),originalPrepare=f.env.DB.prepare.bind(f.env.DB);
  let revoke=true;
  f.env.DB.prepare=sql=>{
    if(revoke && sql==='SELECT * FROM installations WHERE id=?') {
      revoke=false;f.env.DB.sqlite.prepare('UPDATE installations SET epoch=epoch+1,enabled=0 WHERE id=?').run(who.installationId);
    }
    return originalPrepare(sql);
  };
  const stale=await f.post(who,[example(1,'Low')]);assert.equal(stale.status,409);
  assert.deepEqual(f.businessState(),[[],[],[]]);
});

test('preexisting Unknown evidence and its receipts are preserved and cannot be upgraded by replay',async()=>{
  const f=fixture(),who=await f.install();assertReceipt(await f.post(who,[example(1)]),1,0);
  // Model a row accepted by the earlier endpoint without sending new low data
  // through the API. Existing receipt history remains authoritative for replay.
  f.env.DB.sqlite.prepare("UPDATE contributions SET confidence_level='Unknown'").run();
  const legacy=f.businessState();
  assertReceipt(await f.post(who,[example(1)]),0,1);
  assert.deepEqual(f.businessState(),legacy);
  assertReceipt(await f.post(who,[{...example(1,'Low'),analysisId:'new-low-analysis'}]),0,1);
  assert.deepEqual(f.businessState(),legacy);
});
