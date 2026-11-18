import test from 'node:test';
import assert from 'node:assert/strict';
import {handle} from '../src/worker.ts';
import {AGREEMENT_VERSION,validFingerprint,reusableFingerprint,ruleFor,validatedConclusion} from '../src/rules.ts';
import {database} from './sqlite-adapter.mjs';

const id='0123456789abcdef0123456789abcdef';
const directory={softwareId:'generic-v3',versionMajor:'0',roleId:'path3:os-local/mask',shape:`purpose3-observation-directory-${id}`};
const example=(fp,analysisId,purpose='application')=>({...fp,analysisId,purpose,action:'Keep',source:'fresh-ai',providerClass:'official',modelFamily:'deepseek',defaultPrompt:true,sharedContext:false,confidenceLevel:'High'});

function fixture() {
  const env={DB:database(),SERVICE_MODE:'enabled'};let network=0;
  async function call(path,method='GET',body,who) {
    const response=await handle(new Request(`http://localhost${path}`,{method,headers:{'content-type':'application/json',
      'CF-Connecting-IP':`127.0.7.${++network}`,...(who?{'X-Installation-Id':who.installationId,Authorization:`Bearer ${who.managementToken}`}:{})},
      body:body===undefined?undefined:JSON.stringify(body)}),env);
    return {status:response.status,body:await response.json()};
  }
  async function install() {
    const registered=await call('/v1/install','POST',{});assert.equal(registered.status,201);
    const who=registered.body;
    const consent=await call('/v1/consent','PUT',{expectedEpoch:0,enabled:true,agreementVersion:AGREEMENT_VERSION,shareHistory:false,policyRevision:'1'},who);
    assert.equal(consent.status,200);return {...who,epoch:consent.body.epoch};
  }
  return {env,call,install};
}

test('opaque directory and file shapes are valid but can never publish a conclusion',()=>{
  for(const fp of [directory,{...directory,roleId:'path3:os-downloads/file-zip',shape:`purpose3-observation-file-${id}`},
    {...directory,roleId:'path3:os-local/cache'}]) {
    assert.equal(validFingerprint(fp),true);
    assert.equal(reusableFingerprint(fp),false);
    assert.equal(validatedConclusion(ruleFor(fp),'application','Keep'),false);
  }
});

test('opaque identity cannot bypass lowercase length role vocabulary or file-tail validation',()=>{
  for(const fp of [
    {...directory,shape:'purpose3-observation-directory-short'},
    {...directory,shape:`purpose3-observation-directory-${id.toUpperCase()}`},
    {...directory,shape:`purpose3-observation-directory-${id}\n`},
    {...directory,roleId:'path3:os-local/private-name'},
    {...directory,roleId:'path3:os-local/file-zip'},
    {...directory,shape:`purpose3-observation-file-${id}`},
    {...directory,shape:`purpose3-observation-file-${id}`,roleId:'path3:os-local/file-private'}
  ]) assert.equal(validFingerprint(fp),false,JSON.stringify(fp));
});

test('different opaque path identities retain contrary observations without entering cross-installation consensus',async()=>{
  const f=fixture(),author=await f.install(),reader=await f.install();
  const second={...directory,shape:`purpose3-observation-directory-${'b'.repeat(32)}`};
  const submitted=await f.call('/v1/contributions','POST',{epoch:1,items:[example(directory,'opaque_analysis_01','application'),example(second,'opaque_analysis_02','cache')]},author);
  assert.equal(submitted.status,200);assert.equal(submitted.body.accepted,2);
  assert.equal(f.env.DB.sqlite.prepare('SELECT COUNT(*) AS n FROM contributions').get().n,2);
  assert.equal((await f.call('/v1/contributions','POST',{epoch:1,items:[example(directory,'opaque_reader_01')]},reader)).body.accepted,1);
  for(const who of [author,reader]) {
    const query=await f.call('/v1/query','POST',{epoch:1,items:[directory,second]},who);
    assert.equal(query.status,200);
    assert.ok(query.body.items.every(item=>item.matched===false && !('entry' in item)));
  }
  const replay=await f.call('/v1/contributions','POST',{epoch:1,items:[example(directory,'opaque_analysis_01','application'),example(second,'opaque_analysis_02','cache')]},author);
  assert.equal(replay.body.accepted,0);assert.equal(replay.body.skipped,2);
  assert.equal(f.env.DB.sqlite.prepare('SELECT COUNT(*) AS n FROM contributions').get().n,3);
});

test('an existing original analysis receipt cannot be reminted by changing to an opaque shape',async()=>{
  const f=fixture(),author=await f.install();
  const legacy={...directory,shape:'purpose3-directory'};
  assert.equal((await f.call('/v1/contributions','POST',{epoch:1,items:[example(legacy,'original_analysis_01')]},author)).body.accepted,1);
  const replay=await f.call('/v1/contributions','POST',{epoch:1,items:[example(directory,'original_analysis_01')]},author);
  assert.equal(replay.status,200);assert.equal(replay.body.accepted,0);assert.equal(replay.body.skipped,1);
  assert.equal(f.env.DB.sqlite.prepare('SELECT COUNT(*) AS n FROM contributions').get().n,1);
  assert.equal(f.env.DB.sqlite.prepare('SELECT COUNT(*) AS n FROM analysis_receipts').get().n,1);
  assert.equal(f.env.DB.sqlite.prepare('SELECT COUNT(*) AS n FROM buckets').get().n,1);
  assert.equal(f.env.DB.sqlite.prepare('SELECT shape FROM buckets JOIN contributions ON contributions.bucket_key=buckets.key').get().shape,'purpose3-directory');
});
