import test from 'node:test';
import assert from 'node:assert/strict';
import {handle} from '../src/worker.ts';
import {AGREEMENT_VERSION,CONSENSUS} from '../src/rules.ts';
import {validPurposeTextPair} from '../src/purpose-text.ts';
import {database} from './sqlite-adapter.mjs';

const fp={softwareId:'generic-v3',versionMajor:'0',roleId:'path3:os-local/cache',shape:'purpose3-directory'};
const pair={shortPurpose:'应用临时缓存',reason:'应用生成的临时缓存，可在关闭相关程序后清理'};
const example=(id='original-ai-0001')=>({...fp,analysisId:id,purpose:'cache',action:'Review',source:'fresh-ai',
  providerClass:'official',modelFamily:'deepseek',defaultPrompt:true,sharedContext:false,confidenceLevel:'High',...pair});
function fixture() {
  const env={DB:database(),SERVICE_MODE:'enabled'};let network=0;
  async function call(path,method='GET',body,who) {
    const response=await handle(new Request(`http://localhost${path}`,{method,
      headers:{'content-type':'application/json','CF-Connecting-IP':`10.21.0.${++network}`,
        ...(who?{'X-Installation-Id':who.installationId,Authorization:`Bearer ${who.managementToken}`}:{})},
      body:body===undefined?undefined:JSON.stringify(body)}),env);
    return {status:response.status,body:await response.json()};
  }
  async function install(agreementVersion=AGREEMENT_VERSION) {
    const registration=await call('/v1/install','POST',{});assert.equal(registration.status,201);
    const who=registration.body;
    const consent=await call('/v1/consent','PUT',{expectedEpoch:0,enabled:true,agreementVersion,
      shareHistory:false,policyRevision:'original-purpose-texts'},who);
    assert.equal(consent.status,200);return who;
  }
  const query=who=>call('/v1/query','POST',{epoch:1,items:[fp]},who);
  const post=(who,item=example(),epoch=1)=>call('/v1/contributions','POST',{epoch,items:[item]},who);
  return {env,call,install,query,post};
}

test('original two purpose texts survive D1, a second installation query and own-contribution listing unchanged',async()=>{
  const f=fixture(),author=await f.install(),reader=await f.install();
  const result=await f.post(author);assert.equal(result.status,200);assert.equal(result.body.accepted,1);
  const row=f.env.DB.sqlite.prepare('SELECT short_purpose,reason FROM contributions').get();
  assert.equal(row.short_purpose,pair.shortPurpose);assert.equal(row.reason,pair.reason);
  const entry=(await f.query(reader)).body.items[0].entry;
  assert.equal(entry.shortPurpose,pair.shortPurpose);assert.equal(entry.reason,pair.reason);
  const mine=await f.call('/v1/contributions','GET',undefined,author);
  assert.equal(mine.body.items[0].shortPurpose,pair.shortPurpose);assert.equal(mine.body.items[0].reason,pair.reason);
  // Receipt replay must not mutate the original text or manufacture a new vote.
  const replay=await f.post(author,{...example(),shortPurpose:'另一份说明',reason:'这一份文本不能覆盖已收据确认的原始结果'});
  assert.equal(replay.body.accepted,0);assert.equal((await f.query(reader)).body.items[0].entry.reason,pair.reason);
  assert.equal(f.env.DB.sqlite.prepare('SELECT COUNT(*) AS n FROM contributions').get().n,1);
  assert.equal((await f.call('/v1/contributions?epoch=1','DELETE',undefined,author)).status,200);
  assert.equal((await f.query(reader)).body.items[0].matched,false);
});

test('beta2 keeps old query, category contribution and management, but cannot upload dual texts without beta3 consent',async()=>{
  const f=fixture(),older=await f.install('2026-11-beta2'),reader=await f.install();
  assert.equal((await f.post(older)).status,403);
  const legacy=example('legacy-ai-0001');delete legacy.shortPurpose;delete legacy.reason;
  assert.equal((await f.post(older,legacy)).status,200);
  const entry=(await f.query(older)).body.items[0].entry;
  assert.equal(entry.shortPurpose,'');assert.equal(entry.reason,'');
  assert.ok(entry.displayTexts.shortPurpose);assert.ok(entry.displayTexts.reason);
  assert.equal((await f.query(reader)).body.items[0].matched,true);
  const upgrade=await f.call('/v1/consent','PUT',{expectedEpoch:1,enabled:true,agreementVersion:AGREEMENT_VERSION,
    shareHistory:false,policyRevision:'explicit-dual-text-consent'},older);
  assert.equal(upgrade.status,200);assert.equal(upgrade.body.epoch,2);
  assert.equal((await f.post(older,example('original-ai-0002'),2)).status,200);
  assert.equal((await f.query(reader)).body.items[0].entry.shortPurpose,pair.shortPurpose);
  assert.equal((await f.call('/v1/contributions','GET',undefined,older)).status,200);
  assert.equal((await f.call('/v1/contributions?epoch=2','DELETE',undefined,older)).status,200);
});

test('unsafe or incomplete texts are rejected before writing and never silently summarized',async()=>{
  const f=fixture(),author=await f.install();
  const bad=[{shortPurpose:'',reason:'缓存说明'}, {shortPurpose:'缓存',reason:''},
    {shortPurpose:null}, {reason:null}, {shortPurpose:'缓'.repeat(13)}, {reason:'缓'.repeat(31)},
    {reason:'位于C:\\Users\\Alice的缓存'}, {reason:'https://private.test'}, {reason:'联系user@example.com'},
    {reason:'sk-secret-token'}, {reason:'API Key缓存'}, {reason:'用户密码备份'},
    {reason:'手机号13812345678'}, {reason:'<script>敏感内容</script>'}, {reason:'缓存\n用途'},
    {shortPurpose:' 缓存'}, {reason:'缓存 '}, {reason:'D盘的缓存'}];
  for(let index=0;index<bad.length;index++) {
    const response=await f.post(author,{...example(`unsafe-ai-${index}`),...bad[index]});
    assert.equal(response.status,400,JSON.stringify(bad[index]));
    assert.equal(response.body.error,'invalid_purpose_texts');
  }
  assert.equal(f.env.DB.sqlite.prepare('SELECT COUNT(*) AS n FROM contributions').get().n,0);
  assert.equal(f.env.DB.sqlite.prepare('SELECT COUNT(*) AS n FROM analysis_receipts').get().n,0);
});

test('category-only records have separate fallback text, not invented original fields',async()=>{
  const f=fixture(),legacyAuthor=await f.install(),reader=await f.install();
  const legacy=example('legacy-ai-0002');delete legacy.shortPurpose;delete legacy.reason;
  assert.equal((await f.post(legacyAuthor,legacy)).status,200);
  const entry=(await f.query(reader)).body.items[0].entry;
  assert.equal(entry.shortPurpose,'');assert.equal(entry.reason,'');
  assert.ok(entry.displayTexts.shortPurpose);assert.ok(entry.displayTexts.reason);
});

test('qualified text selection preserves one original pair and prefers High over Medium',async()=>{
  const f=fixture(),medium=await f.install(),high=await f.install(),reader=await f.install();
  const other={shortPurpose:'运行时缓存',reason:'程序运行时生成的缓存数据'};
  assert.equal((await f.post(medium,{...example('medium-ai-0001'),confidenceLevel:'Medium',...other})).status,200);
  assert.equal((await f.post(high,example('high-ai-0001'))).status,200);
  let entry=(await f.query(reader)).body.items[0].entry;
  assert.equal(entry.shortPurpose,pair.shortPurpose);assert.equal(entry.reason,pair.reason);
});

test('text validation is a bounded privacy boundary and old migrated rows receive empty defaults',()=>{
  assert.equal(validPurposeTextPair('应用缓存','应用生成的临时数据'),true);
  assert.equal(validPurposeTextPair('',''),true);
  assert.equal(validPurposeTextPair(undefined,undefined),false);
  const db=database();
  const columns=db.sqlite.prepare('PRAGMA table_info(contributions)').all();
  for(const name of ['short_purpose','reason']) {
    const column=columns.find(c=>c.name===name);assert.equal(column.notnull,1);assert.equal(column.dflt_value,"''");
  }
});
