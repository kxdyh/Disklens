import test from 'node:test';
import assert from 'node:assert/strict';
import {handle} from '../src/worker.ts';
import {AGREEMENT_VERSION} from '../src/rules.ts';
import {database} from './sqlite-adapter.mjs';

async function appDataFixture(purposes) {
  const env={DB:database(),SERVICE_MODE:'enabled'};
  const fingerprint={softwareId:'generic-v3',versionMajor:'0',roleId:'path3:os-appdata',shape:'purpose3-directory'};
  let sequence=0;
  async function call(path,method='GET',body,who) {
    const response=await handle(new Request('http://localhost'+path,{method,headers:{'content-type':'application/json',
      'CF-Connecting-IP':`10.43.0.${++sequence}`,
      ...(who?{'X-Installation-Id':who.installationId,Authorization:`Bearer ${who.managementToken}`}:{})},
      body:body===undefined?undefined:JSON.stringify(body)}),env);
    return {status:response.status,body:await response.json()};
  }
  async function install() {
    const registered=await call('/v1/install','POST',{});assert.equal(registered.status,201);
    const who=registered.body;
    const consent=await call('/v1/consent','PUT',{expectedEpoch:0,enabled:true,agreementVersion:AGREEMENT_VERSION,
      shareHistory:false,policyRevision:'unknown-purpose-regression'},who);
    assert.equal(consent.status,200);return who;
  }
  for(const [index,purpose] of purposes.entries()) {
    const author=await install();
    const unknown=purpose==='unknown';
    const contributed=await call('/v1/contributions','POST',{epoch:1,items:[{...fingerprint,
      analysisId:`appdata-purpose-${index}`,purpose,action:'DoNotDelete',confidenceLevel:'High',source:'fresh-ai',
      providerClass:'official',modelFamily:'deepseek',defaultPrompt:true,sharedContext:false,
      shortPurpose:unknown?'用途暂不明确':'用户应用数据',reason:unknown?'需要进一步核验用途':'应用保存的用户数据与配置',
      shortPurposeEn:unknown?'Unknown purpose':'User application data',
      reasonEn:unknown?'The purpose requires further verification.':'User data and configuration saved by applications.'}]},author);
    assert.equal(contributed.status,200);assert.equal(contributed.body.accepted,1);
  }
  const reader=await install();
  return {env,fingerprint,query:()=>call('/v1/query','POST',{epoch:1,items:[fingerprint]},reader)};
}

test('two prior High unknown purposes do not prevent a new known AppData purpose from being reused',async()=>{
  const f=await appDataFixture(['unknown','unknown','user-data']);
  const before=f.env.DB.sqlite.prepare('SELECT * FROM contributions ORDER BY id').all();
  const result=await f.query();assert.equal(result.status,200);assert.equal(result.body.items[0].matched,true);
  const entry=result.body.items[0].entry;
  assert.equal(entry.purpose,'user-data');assert.equal(entry.action,'DoNotDelete');assert.equal(entry.confidenceLevel,'High');
  assert.equal(entry.shortPurpose,'用户应用数据');assert.equal(entry.reason,'应用保存的用户数据与配置');
  assert.equal(entry.shortPurposeEn,'User application data');
  assert.equal(entry.reasonEn,'User data and configuration saved by applications.');
  assert.deepEqual(f.env.DB.sqlite.prepare('SELECT * FROM contributions ORDER BY id').all(),before,
    'unknown observations are retained unchanged rather than deleted or rewritten');
});
