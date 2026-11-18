import test from 'node:test';
import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';
import {handle,maintenance} from '../src/worker.ts';
import {AGREEMENT_VERSION,CONSENSUS,consensusDecision,validFingerprint,reusableFingerprint,ruleFor,validatedConclusion,fingerprintKey,PATH_TOKENS,FILE_EXTENSIONS} from '../src/rules.ts';
import {database} from './sqlite-adapter.mjs';

const fp={softwareId:'android-sdk',versionMajor:'0',roleId:'sdk-temp',shape:'cache-only-v1'};
const example=(id='analysis_0001')=>({...fp,analysisId:id,purpose:'cache',action:'Review',source:'fresh-ai',providerClass:'official',modelFamily:'deepseek',defaultPrompt:true,sharedContext:false,confidenceLevel:'High'});
const generic={softwareId:'generic-v2',versionMajor:'0',roleId:'path2:user-downloads/cache',shape:`tree2-${'a'.repeat(64)}`};
const verifiedApp={...generic,softwareId:`app-${'b'.repeat(64)}`,versionMajor:'2026',roleId:'path2:root'};
function fixture(limits={}) {
  const env={DB:database(),SERVICE_MODE:'enabled',...limits};let network=0;
  async function call(path,method='GET',body,install,headers={}) {
    const request=new Request(`http://localhost${path}`,{method,headers:{...(body!==undefined?{'content-type':'application/json'}:{}),'CF-Connecting-IP':`127.0.0.${++network}`,...(install?{'X-Installation-Id':install.installationId,Authorization:`Bearer ${install.managementToken}`} : {}),...headers},body:body!==undefined?JSON.stringify(body):undefined});
    const response=await handle(request,env);return {status:response.status,body:await response.json(),headers:response.headers};
  }
  async function install(enabled=true,shareHistory=false) {
    const registration=await call('/v1/install','POST',{}); assert.equal(registration.status,201);
    const who=registration.body;
    if(enabled){const consent=await call('/v1/consent','PUT',{expectedEpoch:0,enabled:true,agreementVersion:AGREEMENT_VERSION,shareHistory,policyRevision:'1'},who);assert.equal(consent.status,200);who.epoch=consent.body.epoch;}
    return who;
  }
  return {env,call,install};
}

test('default disabled; management credential is hashed and wrong credential cannot query or withdraw',async()=>{
  const f=fixture(),i=await f.install(false);
  assert.equal(i.epoch,0);
  assert.equal((await f.call('/v1/query','POST',{epoch:0,items:[fp]},i)).status,403);
  assert.equal((await f.call('/v1/consent','GET',undefined,{...i,managementToken:'x'.repeat(43)})).status,401);
  const row=f.env.DB.sqlite.prepare('SELECT token_hash FROM installations').get();
  assert.notEqual(row.token_hash,i.managementToken);assert.match(row.token_hash,/^[a-f0-9]{64}$/);
  const mine=await f.call('/v1/contributions','GET',undefined,i);assert.equal(mine.status,200);assert.ok(!JSON.stringify(mine.body).includes(i.managementToken));
});

test('strict schemas reject path, unknown role, large body and unsafe cloud actions',async()=>{
  const f=fixture(),i=await f.install();
  assert.equal((await f.call('/v1/query','POST',{epoch:1,items:[{...fp,path:'C:\\Users\\private'}]},i)).status,400);
  assert.equal((await f.call('/v1/query','POST',{epoch:1,items:[{...fp,roleId:'personal-temp'}]},i)).status,400);
  assert.equal((await f.call('/v1/contributions','POST',{epoch:1,items:[{...example(),purpose:'runtime',action:'RecycleCandidate'}]},i)).status,400);
  assert.equal((await f.call('/v1/contributions','POST',{epoch:1,items:[{...example(),modelFamily:'my-private-model'}]},i)).status,400);
  assert.equal((await f.call('/v1/query','POST',{epoch:1,items:[fp],padding:'x'.repeat(20000)},i)).status,413);
  assert.equal((await f.call('/v1/query','POST',{epoch:1,items:Array(26).fill(fp)},i)).status,400);
});

test('consent is idempotent and stale changes cannot reverse latest choice',async()=>{
  const f=fixture(),i=await f.install();
  const data={expectedEpoch:0,enabled:true,agreementVersion:AGREEMENT_VERSION,shareHistory:false,policyRevision:'1'};
  assert.equal((await f.call('/v1/consent','PUT',data,i)).body.epoch,1);
  assert.equal((await f.call('/v1/consent','PUT',{...data,enabled:false},i)).status,409);
  assert.equal((await f.call('/v1/consent','PUT',{...data,expectedEpoch:1,enabled:false},i)).body.epoch,2);
  assert.equal((await f.call('/v1/contributions','POST',{epoch:1,items:[example()]},i)).status,409);
});

test('history consent independent; duplicate analysis cannot be a new vote',async()=>{
  const f=fixture(),i=await f.install();
  assert.equal((await f.call('/v1/contributions','POST',{epoch:1,items:[{...example(),source:'historical-ai'}]},i)).status,403);
  let posted=await f.call('/v1/contributions','POST',{epoch:1,items:[example()]},i);assert.equal(posted.status,200);assert.equal(posted.body.accepted,1);
  posted=await f.call('/v1/contributions','POST',{epoch:1,items:[example()]},i);assert.equal(posted.body.accepted,0);
  posted=await f.call('/v1/contributions','POST',{epoch:1,items:[example('analysis_0002')]},i);assert.equal(posted.body.accepted,1);
  assert.equal(f.env.DB.sqlite.prepare('SELECT COUNT(*) AS n FROM contributions').get().n,1);
  const query=await f.call('/v1/query','POST',{epoch:1,items:[fp]},i);assert.equal(query.body.items[0].matched,true);
  assert.equal(query.body.items[0].entry.confidenceLevel,'High');
});

test('withdraw CAS advances epoch, stops late upload and replay; later fresh analysis can contribute',async()=>{
  const f=fixture(),i=await f.install();
  await f.call('/v1/contributions','POST',{epoch:1,items:[example()]},i);
  const id=(await f.call('/v1/contributions','GET',undefined,i)).body.items[0].id;
  const withdrawn=await f.call(`/v1/contributions/${id}?epoch=1`,'DELETE',undefined,i);assert.equal(withdrawn.status,200);assert.equal(withdrawn.body.epoch,2);
  assert.equal((await f.call('/v1/contributions','POST',{epoch:1,items:[example('late_upload')]},i)).status,409);
  assert.equal((await f.call('/v1/contributions?epoch=1','DELETE',undefined,i)).status,409);
  assert.equal((await f.call('/v1/contributions','POST',{epoch:2,items:[example()]},i)).body.accepted,0);
  assert.equal((await f.call('/v1/contributions','POST',{epoch:2,items:[example('brand_new_ai')]},i)).body.accepted,1);
  assert.equal((await f.call('/v1/contributions?epoch=2','DELETE',undefined,i)).body.epoch,3);
});

test('D1-style transaction produces consistent revision; legacy Review stays Review through queries',async()=>{
  const f=fixture(),users=[];
  for(let n=0;n<5;n++){const i=await f.install();users.push(i);await f.call('/v1/contributions','POST',{epoch:1,items:[example(`analysis_${n}000`)]},i);}
  const now=Date.now();
  // Synthetic historical evidence only in an isolated in-memory test database.
  for(let n=0;n<5;n++){
    f.env.DB.sqlite.prepare('UPDATE installations SET created_at=? WHERE id=?').run(now-8*86400000,users[n].installationId);
    f.env.DB.sqlite.prepare('UPDATE contributions SET first_observed=? WHERE installation_id=?').run(now-(4-n)*86400000,users[n].installationId);
  }
  let query=await f.call('/v1/query','POST',{epoch:1,items:[fp]},users[0]);assert.equal(query.status,200);const entry=query.body.items[0].entry;
  assert.equal(entry.action,'Review');assert.equal(entry.ruleVersion,2);assert.equal(entry.confidenceLevel,'High');assert.equal(entry.status,'active');assert.ok(entry.conditions.length>0);
  await f.call('/v1/feedback','POST',{epoch:1,key:entry.key,reason:'unsafe-advice'},users[0]);
  query=await f.call('/v1/query','POST',{epoch:1,items:[fp]},users[0]);assert.equal(query.body.items[0].matched,true,'one report cannot globally freeze a role');
  await f.call('/v1/contributions?epoch=1','DELETE',undefined,users[4]);
  query=await f.call('/v1/query','POST',{epoch:1,items:[fp]},users[0]);assert.equal(query.body.items[0].matched,true,'remaining matching evidence is still usable');
  for (const remaining of users.slice(0,4)) await f.call('/v1/contributions?epoch=1','DELETE',undefined,remaining);
  query=await f.call('/v1/query','POST',{epoch:2,items:[fp]},users[0]);assert.equal(query.body.items[0].matched,false,'withdrawing all evidence invalidates reuse');
});

test('medium/high evidence is reusable immediately across providers and prompt variants',()=>{
  const now=Date.now(),base={purpose:'cache',action:'Review',created_at:now-8*86400000,first_observed:now,provider_class:'official',default_prompt:1,shared_context:0,confidence_level:'High'};
  const rows=Array.from({length:5},(_,n)=>({...base,first_observed:now-n*86400000}));
  assert.ok(consensusDecision(rows,now));assert.ok(consensusDecision(rows.map(r=>({...r,provider_class:'custom'})),now));
  assert.ok(consensusDecision(rows.map(r=>({...r,shared_context:1})),now));
  assert.ok(consensusDecision(rows.map(r=>({...r,default_prompt:0})),now));
  assert.ok(consensusDecision(rows.map(r=>({...r,created_at:now})),now));
  assert.ok(consensusDecision(rows.map(r=>({...r,first_observed:now})),now));
  for (const confidence_level of ['Medium','High']) {
    const decision=consensusDecision([{...base,created_at:now,first_observed:now,confidence_level}],now);
    assert.equal(decision.observations,1);assert.equal(decision.confidenceLevel,confidence_level);
  }
  assert.equal(validFingerprint({...fp,versionMajor:'100'}),false);assert.equal(CONSENSUS.minimumInstallations,1);
  assert.equal(CONSENSUS.minimumAgeHours,0);assert.equal(CONSENSUS.observationHours,0);
});

test('two withdrawals racing on the same epoch have exactly one CAS winner',async()=>{
  const f=fixture(),i=await f.install();
  await f.call('/v1/contributions','POST',{epoch:1,items:[example()]},i);
  const results=await Promise.all([f.call('/v1/contributions?epoch=1','DELETE',undefined,i),f.call('/v1/contributions?epoch=1','DELETE',undefined,i)]);
  assert.deepEqual(results.map(r=>r.status).sort(),[200,409]);
  assert.equal((await f.call('/v1/consent','GET',undefined,i)).body.epoch,2);
  assert.equal((await f.call('/v1/contributions','GET',undefined,i)).body.items.length,0);
});

test('consent changing between authentication and queued contribution prevents all writes',async()=>{
  const f=fixture(),i=await f.install(),original=f.env.DB.batch.bind(f.env.DB);let inject=true;
  f.env.DB.batch=async statements=>{
    if(inject){inject=false;f.env.DB.sqlite.prepare('UPDATE installations SET epoch=epoch+1,enabled=0 WHERE id=?').run(i.installationId);}
    return original(statements);
  };
  const result=await f.call('/v1/contributions','POST',{epoch:1,items:[example()]},i);
  assert.equal(result.status,409);
  assert.equal(f.env.DB.sqlite.prepare('SELECT COUNT(*) AS n FROM contributions').get().n,0);
  assert.equal(f.env.DB.sqlite.prepare('SELECT COUNT(*) AS n FROM analysis_receipts').get().n,0);
});

test('registration throttles only within one minute and maintenance does not expire records',async()=>{
  const f=fixture();
  f.env.DB.sqlite.prepare('UPDATE operator_settings SET registration_limit=3 WHERE id=1').run();
  const sameNetwork={'CF-Connecting-IP':'10.0.0.1'};
  const originalNow=Date.now;
  let now=Date.UTC(2026,0,1,12,0,30);
  Date.now=()=>now;
  try {
    for(let n=0;n<3;n++) assert.equal((await f.call('/v1/install','POST',{},undefined,sameNetwork)).status,201);
    const limited=await f.call('/v1/install','POST',{},undefined,sameNetwork);
    assert.equal(limited.status,429);
    assert.equal(limited.headers.get('Retry-After'),'30');
    now+=30000;
    assert.equal((await f.call('/v1/install','POST',{},undefined,sameNetwork)).status,201);
  } finally {Date.now=originalNow;}
  f.env.DB.sqlite.prepare("INSERT INTO usage_limits(key,count,day) VALUES('old:2000-01-01',1,'2000-01-01')").run();
  await maintenance(f.env);
  assert.equal(f.env.DB.sqlite.prepare("SELECT count(*) AS n FROM usage_limits WHERE day='2000-01-01'").get().n,1);
});

test('historical opt-in is required independently of future sharing and source is preserved',async()=>{
  const f=fixture(),i=await f.install(true,true);
  const result=await f.call('/v1/contributions','POST',{epoch:1,items:[{...example(),source:'historical-ai'}]},i);
  assert.equal(result.status,200);assert.equal(result.body.accepted,1);
  assert.equal((await f.call('/v1/contributions','GET',undefined,i)).body.items[0].source,'historical-ai');
});

test('original analysis id cannot be replayed into another software bucket',async()=>{
  const f=fixture(),i=await f.install();
  assert.equal((await f.call('/v1/contributions','POST',{epoch:1,items:[example()]},i)).body.accepted,1);
  const sameAnalysisOtherRole={...example(),softwareId:'wegame',roleId:'wegame-temp'};
  assert.equal((await f.call('/v1/contributions','POST',{epoch:1,items:[sameAnalysisOtherRole]},i)).body.accepted,0);
  assert.equal((await f.call('/v1/contributions','GET',undefined,i)).body.items.length,1);
});

test('shared runtime records preserve non-delete guidance',async()=>{
  const f=fixture(),users=[];
  const runtime={...fp,roleId:'sdk-emulator',shape:'runtime-v1'};
  for(let n=0;n<10;n++){
    const i=await f.install();users.push(i);
    const item={...example(`runtime_${n}000`),...runtime,purpose:'runtime',action:n===9?'DoNotDelete':'Keep'};
    await f.call('/v1/contributions','POST',{epoch:1,items:[item]},i);
    f.env.DB.sqlite.prepare('UPDATE installations SET created_at=? WHERE id=?').run(Date.now()-10*86400000,i.installationId);
    f.env.DB.sqlite.prepare('UPDATE contributions SET first_observed=? WHERE installation_id=?').run(Date.now()-n*86400000,i.installationId);
  }
  const result=await f.call('/v1/query','POST',{epoch:1,items:[runtime]},users[0]);
  assert.equal(result.body.items[0].matched,true);
  assert.ok(['Keep','DoNotDelete'].includes(result.body.items[0].entry.action));
});

test('old eligible evidence is retained and remains reusable',async()=>{
  const f=fixture(),users=[];
  for(let n=0;n<5;n++){
    const i=await f.install();users.push(i);await f.call('/v1/contributions','POST',{epoch:1,items:[example(`expiry_${n}000`)]},i);
    f.env.DB.sqlite.prepare('UPDATE installations SET created_at=? WHERE id=?').run(Date.now()-40*86400000,i.installationId);
    f.env.DB.sqlite.prepare('UPDATE contributions SET first_observed=?,updated_at=? WHERE installation_id=?').run(Date.now()-35*86400000,Date.now()-31*86400000,i.installationId);
  }
  const result=await f.call('/v1/query','POST',{epoch:1,items:[fp]},users[0]);assert.equal(result.body.items[0].matched,true);
});

test('my contributions support complete cursor pagination without exposing credentials',async()=>{
  const f=fixture(),i=await f.install(),now=Date.now();
  for(let n=1;n<=501;n++){
    const key=`wegame|${n}|wegame-temp|cache-only-v1`;
    f.env.DB.sqlite.prepare('INSERT INTO buckets(key,software_id,version_major,role_id,shape,updated_at) VALUES(?,?,?,?,?,?)').run(key,'wegame',String(n),'wegame-temp','cache-only-v1',now);
    f.env.DB.sqlite.prepare(`INSERT INTO contributions(id,installation_id,analysis_id,bucket_key,purpose,action,source,provider_class,model_family,default_prompt,shared_context,epoch,first_observed,updated_at) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?)`).run(n.toString(16).padStart(64,'0'),i.installationId,`fake_${n}`,key,'cache','Review','fresh-ai','custom','other',1,0,1,now,now);
  }
  const first=await f.call('/v1/contributions','GET',undefined,i);assert.equal(first.status,200);assert.equal(first.body.items.length,500);assert.match(first.body.nextCursor,/^[a-f0-9]{64}$/);
  const second=await f.call(`/v1/contributions?cursor=${first.body.nextCursor}`,'GET',undefined,i);assert.equal(second.body.items.length,1);assert.equal(second.body.nextCursor,null);
  assert.equal(first.body.items.some(x=>x.id===second.body.items[0].id),false);
  assert.ok(!JSON.stringify(first.body).includes(i.managementToken));
  assert.equal((await f.call('/v1/contributions?cursor=private-path','GET',undefined,i)).status,400);
});

test('large buckets still match by exact identity',async()=>{
  const f=fixture(),i=await f.install(),now=Date.now(),key='android-sdk|0|sdk-temp|cache-only-v1';
  f.env.DB.sqlite.prepare('INSERT INTO buckets(key,software_id,version_major,role_id,shape,updated_at) VALUES(?,?,?,?,?,?)').run(key,'android-sdk','0','sdk-temp','cache-only-v1',now);
  for(let n=0;n<1001;n++){
    const installer=`fake-install-${n}`;
    f.env.DB.sqlite.prepare('INSERT INTO installations(id,token_hash,created_at) VALUES(?,?,?)').run(installer,'x',now-8*86400000);
    f.env.DB.sqlite.prepare(`INSERT INTO contributions(id,installation_id,analysis_id,bucket_key,purpose,action,source,provider_class,model_family,default_prompt,shared_context,epoch,first_observed,updated_at,confidence_level) VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)`).run(`vote${n}`,installer,`fake_${n}`,key,'cache','Review','fresh-ai','official','deepseek',1,0,1,now-(n%5)*86400000,now,'High');
  }
  const result=await f.call('/v1/query','POST',{epoch:1,items:[fp]},i);assert.equal(result.status,200);assert.equal(result.body.items[0].matched,true);
});

test('duplicate items in the same upload batch are a validation error, never a partial SQL transaction',async()=>{
  const f=fixture(),i=await f.install();
  const result=await f.call('/v1/contributions','POST',{epoch:1,items:[example(),{...example(),softwareId:'wegame',roleId:'wegame-temp'}]},i);
  assert.equal(result.status,400);assert.equal(result.body.error,'duplicate_batch_items');
  assert.equal(f.env.DB.sqlite.prepare('SELECT COUNT(*) AS n FROM contributions').get().n,0);
});

test('unconfigured/disabled and unknown routes fail before accessing D1; read-only keeps withdrawal available',async()=>{
  let touched=0;
  const disabled={DB:{prepare(){touched++;throw Error('must not query');}}};
  assert.equal((await handle(new Request('http://localhost/v1/rules'),disabled)).status,503);
  assert.equal((await handle(new Request('http://localhost/v1/unknown'),disabled)).status,404);
  assert.equal(touched,0);
  const f=fixture(),i=await f.install();
  await f.call('/v1/contributions','POST',{epoch:1,items:[example()]},i);
  f.env.SERVICE_MODE='read-only';
  assert.equal((await f.call('/v1/contributions','POST',{epoch:1,items:[example('new_ai_0002')]},i)).status,503);
  assert.equal((await f.call('/v1/install','POST',{})).status,503);
  assert.equal((await f.call('/v1/query','POST',{epoch:1,items:[fp]},i)).status,200);
  assert.equal((await f.call('/v1/contributions?epoch=1','DELETE',undefined,i)).body.withdrawn,1);
});

test('D1 storage failure rolls back contribution and does not erase idempotent receipts',async()=>{
  const f=fixture(),i=await f.install();
  assert.equal((await f.call('/v1/contributions','POST',{epoch:1,items:[example()]},i)).body.accepted,1);
  assert.equal((await f.call('/v1/contributions','POST',{epoch:1,items:[example()]},i)).body.accepted,0);
  f.env.DB.sqlite.exec(`CREATE TRIGGER simulate_database_full BEFORE INSERT ON analysis_receipts
    BEGIN SELECT RAISE(ABORT, 'database or disk is full'); END`);
  const full=await f.call('/v1/contributions','POST',{epoch:1,items:[{...example('new_ai_0002'),softwareId:'wegame',roleId:'wegame-temp'}]},i);
  assert.equal(full.status,429);assert.equal(full.body.error,'storage_capacity_paused');
  assert.equal(f.env.DB.sqlite.prepare('SELECT COUNT(*) AS n FROM buckets').get().n,1,'whole batch rolled back');
  assert.equal(f.env.DB.sqlite.prepare("SELECT used FROM resource_caps WHERE name='contributions'").get().used,1);
  assert.equal((await f.call('/v1/contributions?epoch=1','DELETE',undefined,i)).body.withdrawn,1);
  assert.equal(f.env.DB.sqlite.prepare('SELECT COUNT(*) AS n FROM analysis_receipts').get().n,1);
  assert.equal(f.env.DB.sqlite.prepare('SELECT COUNT(*) AS n FROM revocations').get().n,0,'no redundant growing log');
  assert.equal(f.env.DB.sqlite.prepare("SELECT used FROM resource_caps WHERE name='contributions'").get().used,0);
  f.env.DB.sqlite.exec('DROP TRIGGER simulate_database_full');
  assert.equal((await f.call('/v1/contributions','POST',{epoch:2,items:[example()]},i)).body.accepted,0);
});

test('largest allowed contribution request stays within the D1 Free 50-query invocation cap',async()=>{
  const f=fixture(),i=await f.install(),original=f.env.DB.prepare.bind(f.env.DB);let statements=0;
  f.env.DB.prepare=sql=>{statements++;return original(sql);};
  const items=Array.from({length:10},(_,n)=>({...example(`batch_ai_${n}000`),softwareId:'wegame',roleId:'wegame-temp',versionMajor:String(n)}));
  const result=await f.call('/v1/contributions','POST',{epoch:1,items},i);
  assert.equal(result.status,200);assert.equal(result.body.accepted,10);assert.ok(statements<=50,`prepared ${statements} D1 statements`);
});

test('feedback cannot store arbitrary software or directory identifiers',async()=>{
  const f=fixture(),i=await f.install();
  assert.equal((await f.call('/v1/feedback','POST',{epoch:1,key:'private-project|0|user-temp|cache-only-v1',reason:'wrong-owner'},i)).status,400);
  assert.equal(f.env.DB.sqlite.prepare('SELECT COUNT(*) AS n FROM feedback').get().n,0);
});

test('v2 grammar accepts controlled public tokens and extensions, never private path strings',()=>{
  for(const token of PATH_TOKENS) assert.ok(validFingerprint({...verifiedApp,roleId:`path2:${token}`}));
  for(const ext of FILE_EXTENSIONS) assert.ok(validFingerprint({...generic,roleId:`path2:user-downloads/file-${ext}`}));
  assert.ok(validFingerprint({...generic,roleId:`path2:${Array(12).fill('cache').join('/')}`}));
  for(const change of [
    {versionMajor:'1'},{softwareId:'private-project'},{softwareId:`app-${'B'.repeat(64)}`},
    {roleId:'path2:root/cache'},{roleId:'path2:user-downloads/MyProject'},{roleId:'path2:user-downloads/file-secret'},
    {roleId:'path2:file-pdf/cache'},{roleId:'path2:cache//logs'},{roleId:'path2:../cache'},
    {roleId:'C:\\Users\\alice\\Downloads'},{roleId:`path2:${Array(13).fill('cache').join('/')}`},
    {shape:`tree2-${'A'.repeat(64)}`},{shape:'runtime-v1'}
  ]) assert.equal(validFingerprint({...generic,...change}),false,JSON.stringify(change));
  assert.ok(validFingerprint({...generic,shape:`partial2-${'c'.repeat(64)}`}));
});

test('v2 reuse needs complete structure and public context; generic private file extensions do not identify purpose',()=>{
  for(const item of [generic,verifiedApp,{...verifiedApp,roleId:'path2:file-exe'}]) {
    assert.equal(reusableFingerprint(item),true);
    assert.equal(validatedConclusion(ruleFor(item),'downloads','Review'),true);
  }
  for(const item of [
    {...generic,roleId:'path2:root'},{...generic,roleId:'path2:volume'},{...generic,roleId:'path2:volume/file-pdf'},
    {...generic,roleId:'path2:volume/cache/file-tmp'},{...generic,roleId:'path2:user-downloads/file-zip'},
    {...generic,roleId:'path2:file-pdf'},{...generic,roleId:'path2:user-downloads/mask/file-pdf'},
    {...generic,roleId:'path2:user-downloads/file-other'},{...generic,roleId:'path2:user-downloads/file-none'},
    {...verifiedApp,roleId:'path2:file-other'},{...verifiedApp,roleId:'path2:file-none'},
    {...generic,shape:`partial2-${'c'.repeat(64)}`}
  ]) {assert.ok(validFingerprint(item));assert.equal(reusableFingerprint(item),false);}
  for(const purpose of ['cache','runtime','application','configuration','logs','downloads','user-data']) {
    assert.equal(validatedConclusion(ruleFor(generic),purpose,'Keep'),true);
    assert.equal(validatedConclusion(ruleFor(generic),purpose,'DoNotDelete'),true);
    assert.equal(validatedConclusion(ruleFor(generic),purpose,'Review'),!['runtime','user-data'].includes(purpose));
  }
  for(const purpose of ['unknown','my-private-purpose']) assert.equal(validatedConclusion(ruleFor(generic),purpose,'Keep'),false);
});

test('service public path and extension vocabulary exactly matches desktop protocol constants',()=>{
  const source=readFileSync(new URL('../../../src/DiskLens.Core/SharedKnowledge/SharedFingerprintProtocol.cs',import.meta.url),'utf8');
  const readTokens=name=>{
    const match=source.match(new RegExp(`public const string ${name} = "([^"]+)";`));
    assert.ok(match,`${name} must remain a readable public C# constant`);
    const tokens=match[1].split(' ');assert.equal(new Set(tokens).size,tokens.length,`${name} has duplicates`);
    return tokens.sort();
  };
  assert.deepEqual([...PATH_TOKENS].sort(),readTokens('PathTokens'));
  assert.deepEqual([...FILE_EXTENSIONS].sort(),readTokens('ExtensionTokens'));
});

test('only explicit medium/high grades are eligible, with High preferred',()=>{
  const now=Date.now(),rows=Array.from({length:5},(_,n)=>({purpose:'logs',action:'Review',created_at:now-8*86400000,first_observed:now-n*86400000,provider_class:'official',default_prompt:1,shared_context:0,confidence_level:'High'}));
  assert.equal(consensusDecision(rows,now).confidenceLevel,'High');
  assert.equal(consensusDecision(rows.map((r,n)=>({...r,confidence_level:n===4?'Medium':'High'})),now).confidenceLevel,'High');
  for(const grade of ['Low','Unknown',undefined]) assert.equal(consensusDecision(rows.map(r=>({...r,confidence_level:grade})),now),null);
  assert.equal(consensusDecision([...rows,{...rows[0],confidence_level:'Low',action:'Keep',created_at:now}],now).action,'Review');
  assert.equal(consensusDecision([...rows,{...rows[0],confidence_level:'Unknown',action:'DoNotDelete',provider_class:'custom'}],now).action,'Review');
});

test('generic directories deduplicate, reuse eligible advice, accept feedback and invalidate on withdrawal',async()=>{
  const f=fixture(),users=[],now=Date.now();
  const target={...generic,roleId:`path2:${Array(11).fill('user-downloads').join('/')}/downloads`};
  for(let n=0;n<5;n++) {
    const i=await f.install();users.push(i);
    const item={...example(`generic_${n}000`),...target,purpose:'downloads',confidenceLevel:n===4?'Medium':'High'};
    assert.equal((await f.call('/v1/contributions','POST',{epoch:1,items:[item]},i)).body.accepted,1);
    assert.equal((await f.call('/v1/contributions','POST',{epoch:1,items:[item]},i)).body.accepted,0);
    f.env.DB.sqlite.prepare('UPDATE installations SET created_at=? WHERE id=?').run(now-8*86400000,i.installationId);
    f.env.DB.sqlite.prepare('UPDATE contributions SET first_observed=? WHERE installation_id=?').run(now-n*86400000,i.installationId);
  }
  const query=await f.call('/v1/query','POST',{epoch:1,items:[target]},users[0]);
  assert.equal(query.status,200);const entry=query.body.items[0].entry;
  assert.equal(entry.confidenceLevel,'High');assert.equal(entry.purpose,'downloads');assert.equal(entry.ruleVersion,2);
  assert.equal(entry.roleLabel,target.roleId);assert.equal(entry.softwareLabel,'通用文件');
  assert.match(entry.explanation,/下载内容/);assert.match(entry.explanation,/手动确认/);assert.ok(!entry.explanation.includes('WeGame'));
  assert.ok(entry.key.length>160);
  assert.equal((await f.call('/v1/feedback','POST',{epoch:1,key:entry.key,reason:'wrong-role'},users[0])).status,200);
  const mine=await f.call('/v1/contributions','GET',undefined,users[4]);assert.equal(mine.body.items[0].confidenceLevel,'Medium');
  assert.equal(f.env.DB.sqlite.prepare('SELECT COUNT(*) AS n FROM contributions').get().n,5);
  assert.equal((await f.call('/v1/contributions?epoch=1','DELETE',undefined,users[4])).body.withdrawn,1);
  assert.equal((await f.call('/v1/query','POST',{epoch:1,items:[target]},users[0])).body.items[0].entry.confidenceLevel,'High');
  for (const remaining of users.slice(0,4)) await f.call('/v1/contributions?epoch=1','DELETE',undefined,remaining);
  assert.equal((await f.call('/v1/query','POST',{epoch:2,items:[target]},users[0])).body.items[0].matched,false);
});

test('legal v2 categories can be stored; non-reusable identities do not match publicly',async()=>{
  const f=fixture(),users=[],now=Date.now(),cases=[
    {...generic,roleId:'path2:user-downloads/mask/file-pdf',purpose:'downloads'},
    {...generic,shape:`partial2-${'a'.repeat(64)}`,purpose:'downloads'},
    {...generic,roleId:'path2:volume',purpose:'application'},
    {...generic,roleId:'path2:volume/file-pdf',purpose:'downloads'},
    {...generic,roleId:'path2:file-pdf',purpose:'downloads'},
    {...generic,roleId:'path2:user-downloads/file-other',purpose:'downloads'},
    {...generic,roleId:'path2:user-downloads/file-none',purpose:'downloads'},
    {...generic,roleId:'path2:data',purpose:'unknown'},
    {...generic,roleId:'path2:user-documents/file-pdf',purpose:'user-data'},
    {...verifiedApp,roleId:'path2:runtime/file-dll',purpose:'runtime'}
  ];
  for(let n=0;n<5;n++) {
    const i=await f.install();users.push(i);
    const items=cases.map((item,k)=>({...example(`bounded_${n}_${k}00`),...item}));
    assert.equal((await f.call('/v1/contributions','POST',{epoch:1,items},i)).body.accepted,cases.length);
    f.env.DB.sqlite.prepare('UPDATE installations SET created_at=? WHERE id=?').run(now-8*86400000,i.installationId);
    f.env.DB.sqlite.prepare('UPDATE contributions SET first_observed=? WHERE installation_id=?').run(now-n*86400000,i.installationId);
  }
  const items=cases.map(({purpose,...fingerprint})=>fingerprint);
  const result=await f.call('/v1/query','POST',{epoch:1,items},users[0]);assert.equal(result.status,200);
  assert.deepEqual(result.body.items.map(x=>x.matched),items.map(reusableFingerprint));
  assert.equal(f.env.DB.sqlite.prepare('SELECT COUNT(*) AS n FROM contributions').get().n,cases.length*5);
});

test('missing confidence is filtered in both v2 and legacy; accepted analysis replay cannot change its grade',async()=>{
  const f=fixture(),i=await f.install();
  const {confidenceLevel,...item}={...example(),...generic};
  assert.deepEqual((await f.call('/v1/contributions','POST',{epoch:1,items:[item]},i)).body,{epoch:1,accepted:0,skipped:1});
  assert.equal((await f.call('/v1/contributions','GET',undefined,i)).body.items.length,0);
  assert.equal(f.env.DB.sqlite.prepare('SELECT COUNT(*) AS n FROM analysis_receipts').get().n,0);
  assert.equal((await f.call('/v1/contributions','POST',{epoch:1,items:[{...item,confidenceLevel:'Medium'}]},i)).body.accepted,1);
  assert.equal((await f.call('/v1/contributions','POST',{epoch:1,items:[{...item,confidenceLevel:'High'}]},i)).body.accepted,0);
  assert.equal(f.env.DB.sqlite.prepare('SELECT confidence_level FROM contributions').get().confidence_level,'Medium');
  f.env.DB.sqlite.prepare('UPDATE contributions SET first_observed=1').run();
  assert.equal((await f.call('/v1/contributions','POST',{epoch:1,items:[{...item,analysisId:'new_grade_0001',confidenceLevel:'High'}]},i)).body.accepted,1);
  const row=f.env.DB.sqlite.prepare('SELECT confidence_level,first_observed FROM contributions').get();assert.equal(row.confidence_level,'High');assert.ok(row.first_observed>1);
  const legacy={...item,...fp,analysisId:'old_grade_0001'};
  assert.deepEqual((await f.call('/v1/contributions','POST',{epoch:1,items:[legacy]},i)).body,{epoch:1,accepted:0,skipped:1});
  assert.equal(f.env.DB.sqlite.prepare('SELECT confidence_level FROM contributions WHERE bucket_key=?').get(fingerprintKey(fp)),undefined);
  for(const change of [{confidenceLevel:'VeryHigh'},{confidenceLevel:null},{purpose:'private-story'},{normalizedIdentity:'C:\\Users\\alice'},{path:'D:\\secret.pdf'}])
    assert.equal((await f.call('/v1/contributions','POST',{epoch:1,items:[{...item,...change}]},i)).status,400);
});

test('broader v2 scope needs beta2 consent; prior beta1 consent can still list, withdraw and disable',async()=>{
  const f=fixture(),i=await f.install();
  await f.call('/v1/contributions','POST',{epoch:1,items:[example()]},i);
  f.env.DB.sqlite.prepare("UPDATE installations SET agreement_version='2026-11-beta1' WHERE id=?").run(i.installationId);
  assert.equal((await f.call('/v1/query','POST',{epoch:1,items:[generic]},i)).status,403);
  assert.equal((await f.call('/v1/contributions','POST',{epoch:1,items:[{...example('blocked_0001'),...generic}]},i)).status,403);
  const old={expectedEpoch:1,enabled:true,agreementVersion:'2026-11-beta1',shareHistory:false,policyRevision:'1'};
  assert.equal((await f.call('/v1/consent','PUT',old,i)).status,400);
  assert.equal((await f.call('/v1/contributions','GET',undefined,i)).body.items.length,1);
  assert.equal((await f.call('/v1/contributions?epoch=1','DELETE',undefined,i)).body.withdrawn,1);
  assert.equal((await f.call('/v1/consent','PUT',{...old,expectedEpoch:2,enabled:false},i)).body.enabled,false);
  const newConsent=await f.call('/v1/consent','PUT',{...old,expectedEpoch:3,agreementVersion:AGREEMENT_VERSION},i);
  assert.equal(newConsent.body.epoch,4);assert.equal(newConsent.body.agreementVersion,AGREEMENT_VERSION);
  assert.equal((await f.call('/v1/query','POST',{epoch:4,items:[generic]},i)).status,200);
});

test('private file extensions cannot reuse purpose even when all mature advice says Keep or DoNotDelete',async()=>{
  const f=fixture(),users=[],now=Date.now(),targets=[
    {...generic,roleId:'path2:user-downloads/pictures/file-jpg'},
    {...generic,roleId:'path2:user-documents/file-pdf'}
  ];
  for(let n=0;n<5;n++) {
    const i=await f.install();users.push(i);
    const items=targets.map((target,k)=>({...example(`personal_${n}_${k}00`),...target,purpose:'user-data',action:k===0?'Keep':'DoNotDelete'}));
    assert.equal((await f.call('/v1/contributions','POST',{epoch:1,items},i)).body.accepted,2);
    f.env.DB.sqlite.prepare('UPDATE installations SET created_at=? WHERE id=?').run(now-8*86400000,i.installationId);
    f.env.DB.sqlite.prepare('UPDATE contributions SET first_observed=? WHERE installation_id=?').run(now-n*86400000,i.installationId);
  }
  const result=await f.call('/v1/query','POST',{epoch:1,items:targets},users[0]);assert.equal(result.status,200);
  for(let k=0;k<targets.length;k++) {
    const item=result.body.items[k];assert.equal(item.matched,false);
    assert.equal(validatedConclusion(ruleFor(targets[k]),'user-data','Review'),false);
  }
});
