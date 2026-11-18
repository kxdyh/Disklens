import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {handle} from '../src/worker.ts';
import {AGREEMENT_VERSION, PURPOSE_ROOT_TOKENS, PATH_TOKENS, isPurposePathToken, isPathPurposeFingerprint,
  validFingerprint, reusableFingerprint, ruleFor, validatedConclusion, fingerprintKey} from '../src/rules.ts';
import {database} from './sqlite-adapter.mjs';

const os={softwareId:'generic-v3',versionMajor:'0',roleId:'path3:os-local/cache',shape:'purpose3-directory'};
const product={...os,softwareId:`app-${'d'.repeat(64)}`,versionMajor:'42',roleId:'path3:models/weights'};

test('purpose v3 grammar mirrors C# roots and controlled tails and cannot alias tree v2',()=>{
  const core=new URL('../../../src/DiskLens.Core/SharedKnowledge/',import.meta.url);
  const protocol=readFileSync(new URL('SharedFingerprintProtocol.cs',core),'utf8');
  const catalog=readFileSync(new URL('SharedStandardPurposeCatalog.cs',core),'utf8');
  const base=protocol.match(/public const string PurposeRootTokens = "([^"]+)"/);
  const standard=catalog.match(/internal const string RoleTokens = ([^;]+);/);
  assert.ok(base);assert.ok(standard);
  const roots=(base[1]+Array.from(standard[1].matchAll(/"([^"]+)"/g),part=>part[1]).join('')).trim().split(/\s+/);
  assert.deepEqual([...PURPOSE_ROOT_TOKENS].sort(),roots.sort());
  for(const role of PURPOSE_ROOT_TOKENS) assert.equal(reusableFingerprint({...os,roleId:`path3:${role}`}),true);
  for(const token of PATH_TOKENS) {
    const expected=!['volume','root','mask'].includes(token)&&!token.startsWith('user-');
    assert.equal(isPurposePathToken(token),expected);
    assert.equal(validFingerprint({...os,roleId:`path3:os-local/${token}`}),expected||token==='mask');
  }
  for(const item of [os,product,{...product,roleId:'path3:root'}]) {
    assert.equal(validFingerprint(item),true);
    assert.equal(reusableFingerprint(item),true);
    assert.equal(validatedConclusion(ruleFor(item),'application','Keep'),true);
  }
  for(const change of [
    {softwareId:'generic-v2'},{versionMajor:'1'},{shape:`tree2-${'a'.repeat(64)}`},
    {shape:`partial2-${'a'.repeat(64)}`},{roleId:'path2:user-local/cache'},
    {roleId:'path3:os-local/private-project/cache'},
    {roleId:'path3:root'},{roleId:'path3:os-local/os-temp'},
    {roleId:'path3:os-downloads/file-zip'},{roleId:'path3:os-local/user-temp'},
    {roleId:`path3:os-local/${Array(12).fill('cache').join('/')}`}
  ]) assert.equal(validFingerprint({...os,...change}),false,JSON.stringify(change));
  assert.equal(validFingerprint({...product,roleId:'path3:os-local/cache'}),false);
  for(const item of [{...os,roleId:'path3:os-local/mask/cache'},{...os,roleId:'path3:volume/cache'},
    {...product,roleId:'path3:mask/cache'}, {...os,shape:'purpose3-file',roleId:'path3:os-downloads/file-zip'},
    {...product,shape:'purpose3-file',roleId:'path3:cache/file-zip'}]) {
    assert.equal(validFingerprint(item),true);
    assert.equal(reusableFingerprint(item),false);
    assert.equal(validatedConclusion(ruleFor(item),'application','Keep'),false);
  }
  assert.notEqual(fingerprintKey(os),fingerprintKey({...os,softwareId:'generic-v2',roleId:'path2:user-local/cache',shape:`tree2-${'a'.repeat(64)}`}));
  assert.ok(ruleFor(os).conditions.some(x=>x.includes('不构成删除许可')));
  assert.equal(validatedConclusion(ruleFor(os),'user-data','Review'),false);
  assert.equal(validatedConclusion(ruleFor(os),'unknown','Keep'),false);
});

test('legacy private archive observations remain readable without publishing an extension-only purpose',()=>{
  const archive={softwareId:'generic-v2',versionMajor:'0',roleId:'path2:user-downloads/file-zip',shape:`tree2-${'a'.repeat(64)}`};
  assert.equal(validFingerprint(archive),true);
  assert.equal(reusableFingerprint(archive),false);
  assert.equal(validatedConclusion(ruleFor(archive),'downloads','Review'),false);
});

test('independent consenting installation immediately reuses medium/high path purpose, without deletion authority',async()=>{
  const env={DB:database(),SERVICE_MODE:'enabled'};let network=0;
  async function call(path,method='GET',body,who) {
    const response=await handle(new Request(`http://localhost${path}`,{method,headers:{'content-type':'application/json',
      'CF-Connecting-IP':`127.0.1.${++network}`,...(who?{'X-Installation-Id':who.installationId,Authorization:`Bearer ${who.managementToken}`}:{})},
      body:body===undefined?undefined:JSON.stringify(body)}),env);
    return {status:response.status,body:await response.json()};
  }
  async function install() {
    const registration=await call('/v1/install','POST',{});assert.equal(registration.status,201);
    const who=registration.body;
    const consent=await call('/v1/consent','PUT',{expectedEpoch:0,enabled:true,agreementVersion:AGREEMENT_VERSION,shareHistory:false,policyRevision:'1'},who);
    assert.equal(consent.status,200);return {...who,epoch:consent.body.epoch};
  }
  assert.equal((await call('/v1/rules')).body.purposeProtocol,'path3');
  const author=await install(),reader=await install();
  for(const [index,fp] of [os,product].entries()) {
    const confidenceLevel=index?'High':'Medium';
    const item={...fp,analysisId:`pathpurpose_${index}_analysis`,purpose:'application',action:'Keep',source:'fresh-ai',providerClass:'official',modelFamily:'deepseek',defaultPrompt:true,sharedContext:false,confidenceLevel};
    const posted=await call('/v1/contributions','POST',{epoch:1,items:[item]},author);
    assert.equal(posted.status,200);assert.equal(posted.body.accepted,1);
    const query=await call('/v1/query','POST',{epoch:1,items:[fp]},reader);
    assert.equal(query.status,200);assert.equal(query.body.items[0].matched,true);
    const entry=query.body.items[0].entry;
    assert.equal(entry.confidenceLevel,confidenceLevel);assert.equal(entry.action,'Keep');
    assert.equal(entry.roleId,fp.roleId);assert.ok(entry.conditions.some(x=>x.includes('没有核验子树内容')));
    assert.ok(entry.conditions.some(x=>x.includes('不构成删除许可')));
  }
  const observations=[{...os,roleId:'path3:volume/mask/cache'}, {...product,roleId:'path3:mask/cache'},
    {...os,shape:'purpose3-file',roleId:'path3:os-downloads/file-zip'}];
  for(const [index,fp] of observations.entries()) {
    const item={...fp,analysisId:`observation_${index}_analysis`,purpose:'application',action:'Keep',source:'fresh-ai',providerClass:'official',modelFamily:'deepseek',defaultPrompt:true,sharedContext:false,confidenceLevel:'High'};
    const posted=await call('/v1/contributions','POST',{epoch:1,items:[item]},author);
    assert.equal(posted.status,200);assert.equal(posted.body.accepted,1);
    const query=await call('/v1/query','POST',{epoch:1,items:[fp]},reader);
    assert.equal(query.status,200);assert.equal(query.body.items[0].matched,false);
  }
  assert.equal(env.DB.sqlite.prepare('SELECT COUNT(*) AS n FROM contributions').get().n,5);
});
