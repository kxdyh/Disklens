import test from 'node:test';
import assert from 'node:assert/strict';
import {handle} from '../src/worker.ts';
import {AGREEMENT_VERSION, PURPOSES, ruleFor, validatedConclusion, consensusDecision} from '../src/rules.ts';
import {database} from './sqlite-adapter.mjs';

const target={softwareId:'generic-v3',versionMajor:'0',roleId:'path3:os-local/cache',shape:'purpose3-directory'};
const sample=(id='explicit-action-0001')=>({...target,analysisId:id,purpose:'cache',action:'RecycleCandidate',
  source:'fresh-ai',providerClass:'official',modelFamily:'deepseek',defaultPrompt:true,sharedContext:false,
  confidenceLevel:'High',shortPurpose:'应用临时缓存',reason:'应用生成的可重建临时缓存'});
function fixture() {
  const env={DB:database(),SERVICE_MODE:'enabled'};let network=0;
  async function call(path,method='GET',body,who) {
    const response=await handle(new Request(`http://localhost${path}`,{method,headers:{'content-type':'application/json',
      'CF-Connecting-IP':`10.41.0.${++network}`,...(who?{'X-Installation-Id':who.installationId,
        Authorization:`Bearer ${who.managementToken}`}:{})},body:body===undefined?undefined:JSON.stringify(body)}),env);
    return {status:response.status,body:await response.json()};
  }
  async function install(agreementVersion=AGREEMENT_VERSION) {
    const registration=await call('/v1/install','POST',{});assert.equal(registration.status,201);
    const who=registration.body;
    const consent=await call('/v1/consent','PUT',{expectedEpoch:0,enabled:true,agreementVersion,
      shareHistory:false,policyRevision:'explicit-action'},who);
    assert.equal(consent.status,200);return who;
  }
  const post=(who,item=sample())=>call('/v1/contributions','POST',{epoch:1,items:[item]},who);
  const query=(who,items=[target])=>call('/v1/query','POST',{epoch:1,items},who);
  return {env,call,install,post,query};
}

test('explicit recycle is publishable only for cache, logs and downloads across reusable protocols',()=>{
  const fingerprints=[target,
    {softwareId:'generic-v2',versionMajor:'0',roleId:'path2:user-temp/cache',shape:`tree2-${'a'.repeat(64)}`},
    {softwareId:'android-sdk',versionMajor:'0',roleId:'sdk-temp',shape:'cache-only-v1'},
    {softwareId:'wegame',versionMajor:'0',roleId:'wegame-temp',shape:'cache-only-v1'}];
  for(const fingerprint of fingerprints) for(const purpose of PURPOSES) {
    const allowed=['cache','logs','downloads'].includes(purpose)
      && (fingerprint.roleId.startsWith('path')||purpose==='cache');
    assert.equal(validatedConclusion(ruleFor(fingerprint),purpose,'RecycleCandidate'),allowed,
      `${fingerprint.roleId}: ${purpose}`);
  }
  const emulator={softwareId:'android-sdk',versionMajor:'0',roleId:'sdk-emulator',shape:'runtime-v1'};
  assert.equal(validatedConclusion(ruleFor(emulator),'runtime','RecycleCandidate'),false);
  assert.equal(validatedConclusion(ruleFor({...target,roleId:'path3:os-local/mask/cache'}),'cache','RecycleCandidate'),false);
});

test('public protected OS anchors never publish explicit recycle advice',()=>{
  for(const root of ['os-programdata','os-programfiles','os-programfiles-x86']) {
    const fingerprint={...target,roleId:`path3:${root}/cache`};
    assert.equal(validatedConclusion(ruleFor(fingerprint),'cache','RecycleCandidate'),false);
    assert.equal(validatedConclusion(ruleFor(fingerprint),'cache','Review'),true);
    assert.equal(validatedConclusion(ruleFor(fingerprint),'application','DoNotDelete'),true);
  }
});

test('explicit original action survives contribution, D1, independent query and listing without a schema migration',async()=>{
  const f=fixture(),author=await f.install(),reader=await f.install();
  const posted=await f.post(author);assert.equal(posted.status,200);assert.equal(posted.body.accepted,1);
  const row=f.env.DB.sqlite.prepare('SELECT action,short_purpose,reason FROM contributions').get();
  assert.equal(row.action,'RecycleCandidate');assert.equal(row.short_purpose,sample().shortPurpose);
  const query=await f.query(reader);assert.equal(query.status,200);assert.equal(query.body.items[0].matched,true);
  const entry=query.body.items[0].entry;
  assert.equal(entry.action,'RecycleCandidate');assert.equal(entry.purpose,'cache');assert.equal(entry.ruleVersion,2);
  assert.equal(entry.shortPurpose,sample().shortPurpose);assert.equal(entry.reason,sample().reason);
  assert.match(entry.explanation,/可以考虑/);assert.ok(entry.conditions.some(x=>x.includes('不构成删除许可')));
  const mine=await f.call('/v1/contributions','GET',undefined,author);
  assert.equal(mine.body.items[0].action,'RecycleCandidate');
  const replay=await f.post(author,{...sample(),action:'DoNotDelete'});
  assert.equal(replay.body.accepted,0);assert.equal((await f.query(reader)).body.items[0].entry.action,'RecycleCandidate');
});

test('unsafe positive categories are rejected atomically before receipts and SQL changes',async()=>{
  const f=fixture(),author=await f.install();
  for(const purpose of ['runtime','application','configuration','user-data','unknown']) {
    const result=await f.post(author,{...sample(`unsafe-positive-${purpose}`),purpose});
    assert.equal(result.status,400,purpose);
  }
  const invalidBatch=await f.call('/v1/contributions','POST',{epoch:1,items:[sample(),
    {...sample('unsafe-positive-batch'),roleId:'path3:os-local/logs',purpose:'user-data'}]},author);
  assert.equal(invalidBatch.status,400);
  assert.equal(f.env.DB.sqlite.prepare('SELECT COUNT(*) AS n FROM contributions').get().n,0);
  assert.equal(f.env.DB.sqlite.prepare('SELECT COUNT(*) AS n FROM analysis_receipts').get().n,0);
  // Unknown confidence is filtered before recording any new observation.
  const unknown=await f.post(author,{...sample('unknown-negative-0001'),purpose:'unknown',
    action:'DoNotDelete',confidenceLevel:'Unknown'});
  assert.equal(unknown.status,200);assert.equal(unknown.body.accepted,0);assert.equal(unknown.body.skipped,1);
  assert.equal(f.env.DB.sqlite.prepare('SELECT COUNT(*) AS n FROM analysis_receipts').get().n,0);
  assert.equal((await f.query(author)).body.items[0].matched,false);
});

test('explicit logs and downloads recommendations retain their action in independent queries',async()=>{
  const f=fixture(),author=await f.install(),reader=await f.install();
  const rules=await f.call('/v1/rules');assert.ok(rules.body.actions.includes('RecycleCandidate'));
  for(const purpose of ['logs','downloads']) {
    const fingerprint={...target,roleId:`path3:os-local/${purpose}`};
    const item={...sample(`positive-${purpose}-0001`),...fingerprint,purpose,
      shortPurpose:purpose==='logs'?'应用诊断日志':'已下载临时内容',
      reason:purpose==='logs'?'应用产生的诊断日志':'应用下载的临时安装内容'};
    assert.equal((await f.post(author,item)).status,200);
    const entry=(await f.query(reader,[fingerprint])).body.items[0].entry;
    assert.equal(entry.purpose,purpose);assert.equal(entry.action,'RecycleCandidate');
    assert.equal(entry.reason,item.reason);
  }
});

test('legacy Review never becomes recycle, including an older agreement requesting newer explicit advice',async()=>{
  const f=fixture(),author=await f.install(),older=await f.install('2026-11-beta2');
  assert.equal((await f.post(author,{...sample('legacy-review-0001'),action:'Review'})).status,200);
  assert.equal((await f.query(older)).body.items[0].entry.action,'Review');
  assert.equal((await f.post(author,sample('replacement-explicit-0001'))).status,200);
  const entry=(await f.query(older)).body.items[0].entry;
  // No fallback rewriting to Review: an old client's closed action vocabulary
  // must reject this individual entry, while upgraded clients retain the action.
  assert.equal(entry.action,'RecycleCandidate');
  const oldValidConclusion=(purpose,action)=>PURPOSES.includes(purpose)
    && ['Review','Keep','DoNotDelete'].includes(action)&&!(purpose==='runtime'&&action==='Review');
  assert.equal(oldValidConclusion(entry.purpose,entry.action),false);
  assert.equal((await f.query(author)).body.items[0].entry.action,'RecycleCandidate');
});

test('low grades and masked paths never match; protected OS matches never authorize recycling',async()=>{
  const f=fixture(),author=await f.install(),reader=await f.install();
  const cases=[{...sample(),confidenceLevel:'Low'}, {...sample(),roleId:'path3:os-programdata/cache'},
    {...sample(),roleId:'path3:os-local/mask/cache'}];
  for(const [index,item] of cases.entries()) {
    assert.equal((await f.post(author,{...item,analysisId:`unqualified-positive-${index}`})).status,200);
    const {softwareId,versionMajor,roleId,shape}=item;
    const result=(await f.query(reader,[{softwareId,versionMajor,roleId,shape}])).body.items[0];
    if(index===1) assert.equal(result.entry.action,'DoNotDelete');
    else assert.equal(result.matched,false);
  }
});
