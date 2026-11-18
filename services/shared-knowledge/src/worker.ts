import { AGREEMENT_VERSION, RULE_VERSION, RULES, CONSENSUS, PURPOSES, ACTIONS, MAX_ROLE_LENGTH, MAX_FINGERPRINT_KEY_LENGTH, fingerprintKey, validFingerprint, ruleFor, validContributionAction, qualifiedEvidence, explanationFor } from "./rules.ts";
import type { Fingerprint } from "./rules.ts";
import { validPurposeTextPair, validEnglishPurposeTextPair } from "./purpose-text.ts";
import { createSharedPathIndex } from "./path-index.ts";
import { handleAnnouncements } from "./announcements.ts";
import { readOperatorSettings } from "./sharing-settings.ts";
import { handleReleaseFeeds } from "./release-feeds.ts";

// The small local interface keeps unit tests independent of a vendor runtime.
interface Statement {
  bind(...values: unknown[]): Statement;
  first<T = Record<string, unknown>>(): Promise<T | null>;
  all<T = Record<string, unknown>>(): Promise<{results: T[]}>;
  run(): Promise<{meta?: DatabaseMeta}>;
}
interface DatabaseMeta { changes?: number; }
type DatabaseResult = {meta?: DatabaseMeta;results?: Record<string,unknown>[]};
export interface Database { prepare(sql: string): Statement; batch(statements: Statement[]): Promise<DatabaseResult[]>; }
export interface Env {
  DB: Database; SERVICE_MODE?: string;
}
type Obj = Record<string, unknown>;
type Installation = { id: string; epoch: number; enabled: number; agreement_version: string; share_history: number; policy_revision: string; created_at: number; revoked_through_epoch: number; joined_contribution_plan: number };
type Contribution = Fingerprint & {analysisId: string; purpose: string; action: string; source: string; providerClass: string; modelFamily: string; defaultPrompt: boolean; sharedContext: boolean; confidenceLevel: string; shortPurpose: string; reason: string; shortPurposeEn: string; reasonEn: string};
class ApiError extends Error {
  status: number; extra: Obj;
  constructor(status: number, message: string, extra: Obj = {}) { super(message); this.status=status; this.extra=extra; }
}
const json = (data: unknown, status = 200) => new Response(JSON.stringify(data), {status, headers: {"Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store", "X-Content-Type-Options": "nosniff"}});
const fingerprintFields = ["softwareId", "versionMajor", "roleId", "shape"];
const contributionFields = [...fingerprintFields, "analysisId", "purpose", "action", "source", "providerClass", "modelFamily", "defaultPrompt", "sharedContext"];
const nowMs = () => Date.now();
// Cloudflare Free enforces actual D1 row/index writes, reads, storage bytes and
// Worker requests. Do not replace that metering with per-experience estimates
// or write a quota counter for every read-only query.

function object(value: unknown, fields: string[], optionalFields: string[] = []): Obj {
  if (!value || typeof value !== "object" || Array.isArray(value)) throw new ApiError(400, "invalid_object");
  const result = value as Obj;
  if (Object.keys(result).some(key => !fields.includes(key) && !optionalFields.includes(key)) || fields.some(key => !(key in result))) throw new ApiError(400, "unexpected_or_missing_fields");
  return result;
}
function integer(v: unknown): number { if (typeof v !== "number" || !Number.isSafeInteger(v) || v < 0) throw new ApiError(400, "invalid_integer"); return v; }
function boolean(v: unknown): boolean { if (typeof v !== "boolean") throw new ApiError(400, "invalid_boolean"); return v; }
function textValue(v: unknown, re: RegExp, max = 80): string { if (typeof v !== "string" || v.length > max || !re.test(v)) throw new ApiError(400, "invalid_enum_or_id"); return v; }
function list(v: unknown, maximum = 25): unknown[] { if (!Array.isArray(v) || !v.length || v.length > maximum) throw new ApiError(400, `batch_limit_${maximum}`); return v; }
function fingerprint(value: unknown, fields = fingerprintFields, optionalFields: string[] = []): Fingerprint {
  const item = object(value, fields, optionalFields);
  const f = {softwareId: textValue(item.softwareId, /^[a-z0-9-]+$/), versionMajor: textValue(item.versionMajor, /^[0-9]{1,4}$/), roleId: textValue(item.roleId, /^[a-z0-9_:/-]+$/,MAX_ROLE_LENGTH), shape: textValue(item.shape, /^[a-z0-9-]+$/,128)};
  if (!validFingerprint(f)) throw new ApiError(400, "unverified_role");
  return f;
}
async function body(request: Request, maximumBytes = 16384): Promise<unknown> {
  if (!(request.headers.get("content-type") ?? "").toLowerCase().startsWith("application/json")) throw new ApiError(415, "json_required");
  if (Number(request.headers.get("content-length") ?? 0) > maximumBytes) throw new ApiError(413, "body_too_large");
  const reader = request.body?.getReader();
  if (!reader) throw new ApiError(400, "body_required");
  let size = 0; const chunks: Uint8Array[] = [];
  try {
    const deadline = Date.now()+5000;
    for (;;) {
      let timer: ReturnType<typeof setTimeout> | undefined;
      let part: ReadableStreamReadResult<Uint8Array>;
      try {
        part = await Promise.race([reader.read(),new Promise<never>((_resolve,reject)=>{timer=setTimeout(()=>reject(new ApiError(408,"body_timeout")),Math.max(1,deadline-Date.now()));})]);
      } catch(e) { await reader.cancel().catch(()=>{}); throw e; }
      finally { if(timer!==undefined) clearTimeout(timer); }
      if (part.done) break;
      size += part.value.length; if (size > maximumBytes) { await reader.cancel(); throw new ApiError(413, "body_too_large"); } chunks.push(part.value);
    }
    const bytes = new Uint8Array(size); let offset = 0; for (const c of chunks) { bytes.set(c, offset); offset += c.length; }
    return JSON.parse(new TextDecoder("utf-8", {fatal: true}).decode(bytes));
  } catch (e) { if (e instanceof ApiError) throw e; throw new ApiError(400, "invalid_json"); }
}
async function hash(value: string): Promise<string> { return [...new Uint8Array(await crypto.subtle.digest("SHA-256", new TextEncoder().encode(value)))].map(x => x.toString(16).padStart(2, "0")).join(""); }
function token(): string { const bytes = crypto.getRandomValues(new Uint8Array(32)); return btoa(String.fromCharCode(...bytes)).replaceAll("+", "-").replaceAll("/", "_").replaceAll("=", ""); }
function consentResult(i: Installation) { return {epoch: i.epoch, enabled: !!i.enabled, agreementVersion: i.agreement_version, shareHistory: !!i.share_history, policyRevision: i.policy_revision}; }
async function authenticate(request: Request, env: Env): Promise<Installation> {
  const id = textValue(request.headers.get("X-Installation-Id"), /^[a-f0-9-]{36}$/);
  const rawToken = request.headers.get("Authorization") ?? "";
  if (!/^Bearer [A-Za-z0-9_-]{43}$/.test(rawToken)) throw new ApiError(401, "invalid_credential");
  const found = await env.DB.prepare("SELECT id,epoch,enabled,agreement_version,share_history,policy_revision,created_at,revoked_through_epoch,joined_contribution_plan FROM installations WHERE id=? AND token_hash=?").bind(id, await hash(rawToken.slice(7))).first<Installation>();
  if (!found) throw new ApiError(401, "invalid_credential");
  return found;
}
function enabled(i: Installation, epoch: unknown) {
  if (integer(epoch) !== i.epoch) throw new ApiError(409, "stale_epoch", {epoch: i.epoch});
  // beta2 clients may keep using their category-only protocol. Sending the new
  // original model texts is separately gated by explicit beta3 consent below.
  if (!i.enabled || !["2026-11-beta2", AGREEMENT_VERSION].includes(i.agreement_version)) throw new ApiError(403, "consent_required");
}
async function current(env: Env, id: string): Promise<Installation> { const i = await env.DB.prepare("SELECT * FROM installations WHERE id=?").bind(id).first<Installation>(); if (!i) throw new ApiError(401, "invalid_credential"); return i; }
async function dailyLimit(request: Request, env: Env, now: number) {
  const day = new Date(now).toISOString().slice(0,10);
  const {pathname}=new URL(request.url);
  if (request.method === "POST" && pathname === "/v1/install") {
    const settings = await readOperatorSettings(env);
    if (!settings.allow_new_contributors) throw new ApiError(503, "new_contributors_paused");
    const address = request.headers.get("CF-Connecting-IP") ?? "local";
    const window = Math.floor(now / 60000);
    // Short-window hash slots bound temporary accounting to 65536 rows without
    // retaining IPs, permanent network identifiers, or any daily admission cap.
    // A rare collision can throttle only the remainder of this minute.
    const slot = parseInt((await hash(`${window}|${address}`)).slice(0,8),16)%65536;
    const result = await env.DB.prepare(`INSERT INTO registration_windows(slot,window,count) VALUES(?,?,1)
      ON CONFLICT(slot) DO UPDATE SET count=CASE WHEN window=excluded.window THEN count+1 ELSE 1 END,window=excluded.window
      WHERE window<>excluded.window OR count<? RETURNING count`).bind(slot,window,settings.registration_limit).first();
    if (!result) throw new ApiError(429, "registration_rate_limit", { retryAfterSeconds: 60 - Math.floor(now / 1000) % 60 });
  }
  return day;
}

async function changeConsent(request: Request, env: Env, i: Installation, now: number) {
  const data = object(await body(request), ["expectedEpoch", "enabled", "agreementVersion", "shareHistory", "policyRevision"]);
  const expected = integer(data.expectedEpoch), isEnabled = boolean(data.enabled), shareHistory = boolean(data.shareHistory);
  // Old clients can manage or enable their existing category-only protocol;
  // beta1 never consented to the broader file protocol and cannot enable it.
  const agreement = textValue(data.agreementVersion, /^$|^2026-11-beta[123]$/), policy = textValue(data.policyRevision, /^[A-Za-z0-9_-]{0,80}$/);
  if (isEnabled && !["2026-11-beta2", AGREEMENT_VERSION].includes(agreement)) throw new ApiError(400, "agreement_required");
  if (i.enabled === +isEnabled && i.share_history === +shareHistory && i.agreement_version === agreement && i.policy_revision === policy) return json(consentResult(i));
  if (expected !== i.epoch) throw new ApiError(409, "stale_epoch", {epoch: i.epoch});
  if (isEnabled && !i.joined_contribution_plan && !(await readOperatorSettings(env)).allow_new_contributors)
    throw new ApiError(503, "new_contributors_paused");
  const changed = await env.DB.prepare(`UPDATE installations SET epoch=epoch+1,enabled=?,share_history=?,agreement_version=?,policy_revision=?,consent_at=?,
    joined_contribution_plan=CASE WHEN ?=1 THEN 1 ELSE joined_contribution_plan END WHERE id=? AND epoch=?
    AND (?=0 OR joined_contribution_plan=1 OR EXISTS(SELECT 1 FROM operator_settings WHERE id=1 AND allow_new_contributors=1)) RETURNING epoch`)
    .bind(+isEnabled,+shareHistory,agreement,policy,now,+isEnabled,i.id,expected,+isEnabled).first();
  if (!changed) throw new ApiError(409, "stale_epoch", {epoch: (await current(env,i.id)).epoch});
  return json(consentResult(await current(env,i.id)));
}

function parseContributions(values: unknown, i: Installation): Contribution[] {
  const items = list(values,10).map(value => {
    const f = fingerprint(value, contributionFields, ["confidenceLevel", "shortPurpose", "reason", "shortPurposeEn", "reasonEn"]), o = value as Obj;
    const shortPurpose = "shortPurpose" in o ? o.shortPurpose : "", reason = "reason" in o ? o.reason : "";
    if (!validPurposeTextPair(shortPurpose, reason)) throw new ApiError(400, "invalid_purpose_texts");
    const shortPurposeEn = "shortPurposeEn" in o ? o.shortPurposeEn : "", reasonEn = "reasonEn" in o ? o.reasonEn : "";
    if (!validEnglishPurposeTextPair(shortPurposeEn, reasonEn) || (shortPurposeEn !== "" && shortPurpose === "")) throw new ApiError(400, "invalid_english_purpose_texts");
    if ((shortPurpose !== "" || shortPurposeEn !== "") && i.agreement_version !== AGREEMENT_VERSION) throw new ApiError(403, "purpose_text_consent_required");
    const item: Contribution = {...f, analysisId: textValue(o.analysisId,/^[A-Za-z0-9_-]{8,80}$/), purpose: textValue(o.purpose,/^(cache|runtime|application|configuration|logs|downloads|user-data|unknown)$/), action: textValue(o.action,/^(Review|Keep|DoNotDelete|RecycleCandidate)$/), source: textValue(o.source,/^(fresh-ai|historical-ai)$/), providerClass: textValue(o.providerClass,/^(official|custom)$/), modelFamily: textValue(o.modelFamily,/^(deepseek|openai|ollama|other)$/), defaultPrompt: boolean(o.defaultPrompt), sharedContext: boolean(o.sharedContext), confidenceLevel: "confidenceLevel" in o ? textValue(o.confidenceLevel,/^(High|Medium|Low|Unknown)$/) : "Unknown", shortPurpose: shortPurpose as string, reason: reason as string, shortPurposeEn: shortPurposeEn as string, reasonEn: reasonEn as string};
    if (!validContributionAction(item.purpose, item.action)) throw new ApiError(400, "unsafe_recycle_category");
    if (item.source === "historical-ai" && !i.share_history) throw new ApiError(403,"history_not_consented");
    return item;
  });
  if (new Set(items.map(x=>x.analysisId)).size !== items.length || new Set(items.map(fingerprintKey)).size !== items.length) throw new ApiError(400,"duplicate_batch_items");
  return items;
}

function appendNewContributionStatements(env: Env, i: Installation, item: Contribution, key: string,
  id: string, requestId: string, now: number, statements: Statement[]) {
  statements.push(env.DB.prepare("INSERT INTO analysis_receipts(installation_id,analysis_id,request_id,received_at) SELECT ?,?,?,? WHERE EXISTS(SELECT 1 FROM installations WHERE id=? AND epoch=? AND enabled=1) ON CONFLICT DO NOTHING").bind(i.id,item.analysisId,requestId,now,i.id,i.epoch));
  // A receipt replay under a newly assigned observation shape is still the
  // same original analysis. Do not create even an empty new bucket for it.
  statements.push(env.DB.prepare("INSERT INTO buckets(key,software_id,version_major,role_id,shape,updated_at) SELECT ?,?,?,?,?,? WHERE EXISTS(SELECT 1 FROM installations WHERE id=? AND epoch=? AND enabled=1) AND EXISTS(SELECT 1 FROM analysis_receipts WHERE installation_id=? AND analysis_id=? AND request_id=?) ON CONFLICT(key) DO NOTHING").bind(key,item.softwareId,item.versionMajor,item.roleId,item.shape,now,i.id,i.epoch,i.id,item.analysisId,requestId));
  statements.push(env.DB.prepare(`INSERT INTO contributions(id,installation_id,analysis_id,bucket_key,purpose,action,source,provider_class,model_family,default_prompt,shared_context,epoch,first_observed,updated_at,confidence_level,short_purpose,reason,short_purpose_en,reason_en)
    SELECT COALESCE((SELECT id FROM contributions WHERE installation_id=? AND bucket_key=?),?),?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,? WHERE EXISTS(SELECT 1 FROM installations WHERE id=? AND epoch=? AND enabled=1) AND EXISTS(SELECT 1 FROM analysis_receipts WHERE installation_id=? AND analysis_id=? AND request_id=?)
    ON CONFLICT(installation_id,bucket_key) DO UPDATE SET analysis_id=excluded.analysis_id,purpose=excluded.purpose,action=excluded.action,source=excluded.source,provider_class=excluded.provider_class,model_family=excluded.model_family,default_prompt=excluded.default_prompt,shared_context=excluded.shared_context,confidence_level=excluded.confidence_level,short_purpose=excluded.short_purpose,reason=excluded.reason,short_purpose_en=excluded.short_purpose_en,reason_en=excluded.reason_en,epoch=excluded.epoch,first_observed=CASE WHEN contributions.withdrawn=1 OR contributions.purpose<>excluded.purpose OR contributions.action<>excluded.action OR contributions.confidence_level<>excluded.confidence_level THEN excluded.first_observed ELSE contributions.first_observed END,updated_at=excluded.updated_at,withdrawn=0`).bind(i.id,key,id,i.id,item.analysisId,key,item.purpose,item.action,item.source,item.providerClass,item.modelFamily,+item.defaultPrompt,+item.sharedContext,i.epoch,now,now,item.confidenceLevel,item.shortPurpose,item.reason,item.shortPurposeEn,item.reasonEn,i.id,i.epoch,i.id,item.analysisId,requestId));
  statements.push(env.DB.prepare("UPDATE buckets SET revision=revision+1,updated_at=? WHERE key=? AND EXISTS(SELECT 1 FROM analysis_receipts WHERE installation_id=? AND analysis_id=? AND request_id=?) AND EXISTS(SELECT 1 FROM installations WHERE id=? AND epoch=? AND enabled=1)").bind(now,key,i.id,item.analysisId,requestId,i.id,i.epoch));
}

async function contribute(request: Request, env: Env, i: Installation, now: number, day: string) {
  const data = object(await body(request), ["epoch", "items"]); enabled(i,data.epoch);
  const items = parseContributions(data.items,i);
  // Validate the complete original batch before filtering so low-confidence
  // duplicates or malformed fields cannot bypass the request contract. Grades
  // are client assertions, not proof of correctness or a replacement for limits.
  const eligible = items.filter(item => item.confidenceLevel === "Medium" || item.confidenceLevel === "High");
  if (!eligible.length) {
    const final = await current(env,i.id); enabled(final,i.epoch);
    return json({epoch:final.epoch,accepted:0,skipped:items.length});
  }
  type ExistingContribution = {id:string;analysis_id:string;bucket_key:string;epoch:number;withdrawn:number;shape:string};
  const sharedItems = eligible.filter(item=>/^purpose3-shared-(directory|file)-[a-f0-9]{64}$/.test(item.shape));
  const existing = sharedItems.length ? (await env.DB.prepare(`SELECT c.id,c.analysis_id,c.bucket_key,c.epoch,c.withdrawn,b.shape
    FROM contributions c JOIN buckets b ON b.key=c.bucket_key WHERE c.installation_id=?
      AND (c.analysis_id IN (${sharedItems.map(()=>"?").join(",")}) OR c.bucket_key IN (${sharedItems.map(()=>"?").join(",")}))`)
    .bind(i.id,...sharedItems.map(item=>item.analysisId),...sharedItems.map(fingerprintKey)).all<ExistingContribution>()).results : [];
  const requestId = crypto.randomUUID(); const statements: Statement[] = [];
  const upgrades: {analysisId:string;statement:number}[] = [];
  const blockedUpgrades: {analysisId:string;reason:string}[] = [];
  for (const item of eligible) {
    // Retain existing bounded contrary advice instead of fabricating agreement.
    // Explicit positive actions passed category checks before any SQL mutation;
    // publication separately enforces path reuse and protected-anchor boundaries.
    // Existing rows keep their opaque ID through the bucket upsert. New rows
    // also bind the original analysis so an older client's newly analyzed
    // legacy key cannot collide with a row whose public identity was upgraded.
    const key = fingerprintKey(item), id = await hash(`${i.id}|${key}|${item.analysisId}`);
    const previous = sharedItems.includes(item) ? existing.find(row=>row.analysis_id===item.analysisId) : undefined;
    if (previous && previous.bucket_key!==key) {
      const previousKind = /^purpose3-observation-(directory|file)-[a-f0-9]{32}$/.exec(previous.shape)?.[1];
      const nextKind = /^purpose3-shared-(directory|file)-[a-f0-9]{64}$/.exec(item.shape)?.[1];
      if (!previousKind || previousKind!==nextKind
          || previous.withdrawn!==0 || previous.epoch<=i.revoked_through_epoch) {
        blockedUpgrades.push({analysisId:item.analysisId,reason:"original_not_upgradeable"});
        continue;
      }
      if (existing.some(row=>row.bucket_key===key && row.id!==previous.id)) {
        blockedUpgrades.push({analysisId:item.analysisId,reason:"upgrade_target_occupied"});
        continue;
      }
      // This authenticated identity-only upgrade is not another AI analysis.
      // Keep its immutable ID, receipt and contribution epoch. The incoming
      // original-analysis payload has passed the same text/privacy contract;
      // use it to restore texts that an older upload had left empty.
      const sourceExists = `EXISTS(SELECT 1 FROM contributions c JOIN installations owner ON owner.id=c.installation_id
        WHERE c.id=? AND c.installation_id=? AND c.analysis_id=? AND c.bucket_key=? AND c.epoch=?
          AND c.withdrawn=0 AND c.epoch>owner.revoked_through_epoch AND owner.epoch=? AND owner.enabled=1)`;
      const sourceValues = [previous.id,i.id,item.analysisId,previous.bucket_key,previous.epoch,i.epoch];
      const targetFree = "NOT EXISTS(SELECT 1 FROM contributions WHERE installation_id=? AND bucket_key=? AND id<>?)";
      const targetValues = [i.id,key,previous.id];
      statements.push(env.DB.prepare(`INSERT INTO buckets(key,software_id,version_major,role_id,shape,updated_at)
        SELECT ?,?,?,?,?,? WHERE ${sourceExists} AND ${targetFree} ON CONFLICT(key) DO NOTHING`)
        .bind(key,item.softwareId,item.versionMajor,item.roleId,item.shape,now,...sourceValues,...targetValues));
      statements.push(env.DB.prepare(`UPDATE buckets SET revision=revision+1,updated_at=?
        WHERE key IN (?,?) AND ${sourceExists} AND ${targetFree}`)
        .bind(now,previous.bucket_key,key,...sourceValues,...targetValues));
      upgrades.push({analysisId:item.analysisId,statement:statements.length});
      statements.push(env.DB.prepare(`UPDATE contributions SET bucket_key=?,updated_at=?,purpose=?,action=?,source=?,
        provider_class=?,model_family=?,default_prompt=?,shared_context=?,confidence_level=?,
        short_purpose=?,reason=?,short_purpose_en=?,reason_en=?
        WHERE id=? AND ${sourceExists} AND ${targetFree} RETURNING id`)
        .bind(key,now,item.purpose,item.action,item.source,item.providerClass,item.modelFamily,+item.defaultPrompt,
          +item.sharedContext,item.confidenceLevel,item.shortPurpose,item.reason,item.shortPurposeEn,item.reasonEn,
          previous.id,...sourceValues,...targetValues));
      continue;
    }
    appendNewContributionStatements(env,i,item,key,id,requestId,now,statements);
  }
  if (blockedUpgrades.length) throw new ApiError(409,"contribution_upgrade_conflict",{items:blockedUpgrades});
  const written = statements.length ? await env.DB.batch(statements) : [];
  const migrated = upgrades.filter(upgrade=>written[upgrade.statement]?.results?.length===1);
  for (const upgrade of upgrades) if (!migrated.includes(upgrade))
    blockedUpgrades.push({analysisId:upgrade.analysisId,reason:"upgrade_not_applied"});
  const final = await current(env,i.id); enabled(final,i.epoch);
  if (blockedUpgrades.length) throw new ApiError(409,"contribution_upgrade_conflict",{items:blockedUpgrades});
  const accepted = await env.DB.prepare("SELECT COUNT(*) AS count FROM analysis_receipts WHERE installation_id=? AND request_id=?").bind(i.id,requestId).first<{count:number}>();
  const confirmed = (accepted?.count ?? 0)+migrated.length;
  return json({epoch:final.epoch,accepted:confirmed,skipped:items.length-confirmed,
    migrated:migrated.length,blockedUpgrades});
}

type Evidence = {id:string;bucket_key:string;purpose:string;action:string;first_observed:number;provider_class:string;default_prompt:number;shared_context:number;confidence_level:string;short_purpose:string;reason:string;short_purpose_en:string;reason_en:string;updated_at:number;created_at:number};
type SharedBucket = {key:string;revision:number;updated_at:number};
const evidenceSql = `SELECT c.id,c.bucket_key,c.purpose,c.action,c.first_observed,c.provider_class,c.default_prompt,c.shared_context,c.confidence_level,c.short_purpose,c.reason,c.short_purpose_en,c.reason_en,c.updated_at,i.created_at
      FROM contributions c JOIN installations i ON c.installation_id=i.id AND c.epoch>i.revoked_through_epoch`;

// Legacy shared rows may contain only the controlled purpose category.  Keep
// those rows reusable and provide a separate bilingual display pair at read
// time; original model fields remain untouched for older clients and caches.
function categoryPurposeTexts(purpose: string): {shortPurpose: string; reason: string; shortPurposeEn: string; reasonEn: string} {
  const labels: Record<string, [string, string, string, string]> = {
    cache: ["缓存或临时数据", "应用缓存与临时数据", "Application cache", "Application cache and temporary data"],
    runtime: ["运行依赖", "程序运行依赖文件", "Runtime dependencies", "Application runtime dependencies"],
    application: ["应用组件", "应用程序组件", "Application components", "Application components"],
    configuration: ["配置数据", "应用配置数据", "Configuration data", "Application configuration data"],
    logs: ["日志记录", "程序运行日志", "Log records", "Application logs"],
    downloads: ["下载内容", "下载内容", "Downloaded content", "Downloaded content"],
    "user-data": ["用户资料", "用户个人资料", "User data", "Personal user data"],
    unknown: ["用途暂不明确", "用途暂不明确", "Purpose unclear", "Purpose unclear"]
  };
  const [shortPurpose, reason, shortPurposeEn, reasonEn] = labels[purpose] ?? labels.unknown;
  return {shortPurpose, reason, shortPurposeEn, reasonEn};
}

function sharedQueryItems(items: Fingerprint[], rows: Evidence[], buckets: SharedBucket[], now: number): Obj[] {
  const responses: Obj[] = [];
  for (const f of items) {
    const key = fingerprintKey(f), rule = ruleFor(f)!;
    // Old installation-random observations cannot be guessed into public keys.
    if (rule.reusable === false) {responses.push({key,matched:false});continue;}
    const usableText = (row: Evidence) => row.short_purpose !== "" && row.reason !== ""
      && validPurposeTextPair(row.short_purpose,row.reason);
    const evidence = rows.filter(r=>r.bucket_key===key && qualifiedEvidence(r,now)
      && (PURPOSES as readonly string[]).includes(r.purpose) && validContributionAction(r.purpose,r.action))
      .sort((a,b)=>Number(usableText(b))-Number(usableText(a))
        || Number(b.confidence_level === "High")-Number(a.confidence_level === "High")
        || b.updated_at-a.updated_at || a.id.localeCompare(b.id));
    const selected = evidence[0];
    const bucket = buckets.find(b=>b.key===key);
    if (!selected || !bucket) {responses.push({key,matched:false});continue;}
    // Keep all original fields on one analysis. Category fallback has its own
    // display-only object so it cannot be mistaken for original model text.
    const categoryText = categoryPurposeTexts(selected.purpose);
    const original = usableText(selected) ? selected : undefined;
    const hasOriginalEnglish = !!original && original.short_purpose_en !== "" && original.reason_en !== ""
      && validEnglishPurposeTextPair(original.short_purpose_en, original.reason_en);
    const english = hasOriginalEnglish
      ? {shortPurposeEn: original!.short_purpose_en, reasonEn: original!.reason_en} : categoryText;
    // Cloud publication never relaxes protected-location handling. Preserve the
    // purpose while constraining an incompatible handling recommendation.
    const action = rule.actions.includes(selected.action) ? selected.action : "DoNotDelete";
    const entry = {key,softwareId:f.softwareId,softwareLabel:rule.softwareLabel,roleId:f.roleId,roleLabel:rule.roleLabel,
      purpose:selected.purpose,action,confidenceLevel:selected.confidence_level,conditions:rule.conditions,explanation:explanationFor(selected.purpose,action),
      shortPurpose:original?.short_purpose ?? "",reason:original?.reason ?? "",
      shortPurposeEn:hasOriginalEnglish ? original!.short_purpose_en : "",
      reasonEn:hasOriginalEnglish ? original!.reason_en : "",
      displayTexts:{shortPurpose:original?.short_purpose ?? categoryText.shortPurpose,
        reason:original?.reason ?? categoryText.reason,shortPurposeEn:english.shortPurposeEn,reasonEn:english.reasonEn},
      ruleVersion:/^purpose3-shared-/.test(f.shape)?RULE_VERSION:2,revision:bucket.revision,status:"active",expiresAt:new Date(now+15*60000).toISOString(),updatedAt:new Date(bucket.updated_at).toISOString()};
    responses.push({key,matched:true,entry});
  }
  return responses;
}

async function query(request: Request, env: Env, i: Installation, now: number) {
  const data = object(await body(request), ["epoch","items"]); enabled(i,data.epoch);
  const items = list(data.items).map(v => fingerprint(v));
  const keys = [...new Set(items.map(fingerprintKey))], placeholders = keys.map(()=>"?").join(",");
  // Rank only the requested indexed buckets inside D1. Return one original
  // record per identity, so a popular bucket cannot truncate an entire batch.
  // Uploaded texts have already passed the shared text contract; reads still
  // validate them again before exposing any original free text.
  const snapshot = await env.DB.batch([
    env.DB.prepare(`SELECT key,revision,updated_at FROM buckets WHERE key IN (${placeholders})`).bind(...keys),
    env.DB.prepare(`WITH ranked AS (
      SELECT c.id,c.bucket_key,c.purpose,c.action,c.first_observed,c.provider_class,c.default_prompt,c.shared_context,
        c.confidence_level,c.short_purpose,c.reason,c.short_purpose_en,c.reason_en,c.updated_at,i.created_at,
        ROW_NUMBER() OVER(PARTITION BY c.bucket_key ORDER BY
          CASE WHEN c.short_purpose<>'' AND c.reason<>'' THEN 0 ELSE 1 END,
          CASE WHEN c.confidence_level='High' THEN 0 ELSE 1 END,c.updated_at DESC,c.id ASC) AS position
      FROM contributions c JOIN installations i ON c.installation_id=i.id
      WHERE c.bucket_key IN (${placeholders}) AND c.withdrawn=0 AND c.epoch>i.revoked_through_epoch
        AND c.confidence_level IN ('High','Medium')
    ) SELECT * FROM ranked WHERE position=1`).bind(...keys)
  ]);
  const responses = sharedQueryItems(items, (snapshot[1].results ?? []) as Evidence[],
    (snapshot[0].results ?? []) as SharedBucket[], now);
  // Do not return a result to a request whose consent changed while reading.
  enabled(await current(env,i.id),i.epoch);
  return json({items:responses});
}

async function queryOwnObservations(request: Request, env: Env, i: Installation, now: number) {
  const data = object(await body(request),["epoch","items"]); enabled(i,data.epoch);
  const items = list(data.items).map(value=>fingerprint(value));
  if (items.some(item=>!/^purpose3-observation-(directory|file)-[a-f0-9]{32}$/.test(item.shape)))
    throw new ApiError(400,"observation_fingerprint_required");
  const keys = [...new Set(items.map(fingerprintKey))], placeholders = keys.map(()=>"?").join(",");
  // A reviewed identity upgrade preserves the original opaque row ID. Older
  // clients can still restore their own observation after its bucket moves;
  // this never grants access to another installation or creates public votes.
  const originalIds = await Promise.all(keys.map(key=>hash(`${i.id}|${key}`)));
  const snapshot = await env.DB.batch([
    env.DB.prepare(`SELECT key,revision,updated_at FROM buckets WHERE key IN (${placeholders})`).bind(...keys),
    env.DB.prepare(`${evidenceSql} WHERE c.installation_id=? AND (c.bucket_key IN (${placeholders}) OR c.id IN (${placeholders}))
      AND c.withdrawn=0 LIMIT 51`).bind(i.id,...keys,...originalIds)
  ]);
  const buckets = (snapshot[0].results ?? []) as SharedBucket[], evidence = (snapshot[1].results ?? []) as Evidence[];
  const responses = items.map(f=>{
    const key = fingerprintKey(f), bucket = buckets.find(row=>row.key === key);
    const direct = evidence.filter(row=>row.bucket_key === key);
    const matching = direct.length ? direct : evidence.filter(row=>row.id === originalIds[keys.indexOf(key)]);
    const row = matching.length === 1 ? matching[0] : undefined, rule = ruleFor(f)!;
    if (!bucket || evidence.length > 50 || !row || !qualifiedEvidence(row,now) || row.purpose === "unknown"
      || !(PURPOSES as readonly string[]).includes(row.purpose) || !validContributionAction(row.purpose,row.action)
      || !row.short_purpose || !row.reason || !validPurposeTextPair(row.short_purpose,row.reason)
      || !validEnglishPurposeTextPair(row.short_purpose_en,row.reason_en)) return {key,matched:false};
    // This is an authenticated installation restoring its own exact anonymous
    // observation, never a cross-installation match or a new consensus vote.
    const entry = {key,softwareId:f.softwareId,softwareLabel:rule.softwareLabel,roleId:f.roleId,roleLabel:rule.roleLabel,
      purpose:row.purpose,action:row.action,confidenceLevel:row.confidence_level,
      conditions:["当前安装自身已上传的原始观察；不是跨用户用途共识。",...rule.conditions],
      explanation:"同一安装的匿名观察标识完全匹配；请结合本机当前内容核验。",
      shortPurpose:row.short_purpose,reason:row.reason,shortPurposeEn:row.short_purpose_en,reasonEn:row.reason_en,
      ruleVersion:2,revision:bucket.revision,status:"active",expiresAt:new Date(now+15*60000).toISOString(),
      updatedAt:new Date(bucket.updated_at).toISOString(),isOwnObservation:true};
    return {key,matched:true,entry};
  });
  enabled(await current(env,i.id),i.epoch);
  return json({items:responses});
}

async function mine(request: Request, env: Env, i: Installation) {
  const parameters = new URL(request.url).searchParams;
  parameters.forEach((_value,key)=>{if(key!=="cursor") throw new ApiError(400,"unexpected_fields");});
  const cursor = parameters.has("cursor") ? textValue(parameters.get("cursor"),/^[a-f0-9]{64}$/) : "";
  const rows = await env.DB.prepare(`SELECT c.id,c.analysis_id,c.bucket_key,c.purpose,c.action,c.source,c.confidence_level,c.short_purpose,c.reason,c.short_purpose_en,c.reason_en,c.updated_at,c.withdrawn,
    b.software_id,b.version_major,b.role_id,b.shape FROM contributions c JOIN buckets b ON c.bucket_key=b.key
    JOIN installations i ON c.installation_id=i.id AND c.epoch>i.revoked_through_epoch
    WHERE c.installation_id=? AND c.withdrawn=0 AND c.id>? ORDER BY c.id ASC LIMIT 501`).bind(i.id,cursor).all<{id:string;analysis_id:string;bucket_key:string;purpose:string;action:string;source:string;confidence_level:string;short_purpose:string;reason:string;short_purpose_en:string;reason_en:string;updated_at:number;withdrawn:number;software_id:string;version_major:string;role_id:string;shape:string}>();
  const page = rows.results.slice(0,500), nextCursor = rows.results.length>500 ? page[499].id : null;
  return json({epoch:i.epoch,nextCursor,items:page.map(r => ({id:r.id,analysisId:r.analysis_id,key:r.bucket_key,purpose:r.purpose,action:r.action,source:r.source,confidenceLevel:r.confidence_level,shortPurpose:r.short_purpose,reason:r.reason,shortPurposeEn:r.short_purpose_en,reasonEn:r.reason_en,updatedAt:new Date(r.updated_at).toISOString(),withdrawn:!!r.withdrawn,softwareId:r.software_id,versionMajor:r.version_major,roleId:r.role_id,shape:r.shape}))});
}

function withdrawalEpoch(request: Request, i: Installation): number {
  const rawEpoch = new URL(request.url).searchParams.get("epoch");
  if (rawEpoch === null || !/^\d+$/.test(rawEpoch)) throw new ApiError(400,"epoch_required");
  const expected = Number(rawEpoch);
  if (!Number.isSafeInteger(expected) || expected !== i.epoch) throw new ApiError(409,"stale_epoch",{epoch:i.epoch});
  return expected;
}

// Only an explicit authenticated withdrawal calls this bounded cleanup. A
// revoked epoch never becomes visible again, even if cleanup is interrupted.
async function cleanupWithdrawnPage(env: Env, i: Installation, now: number, expected: number, floor: number) {
  const op = crypto.randomUUID(), guard = "EXISTS(SELECT 1 FROM epoch_guards WHERE operation_id=?)";
  const page = "SELECT id FROM contributions WHERE installation_id=? AND epoch<=? ORDER BY epoch,id LIMIT 200";
  const result = await env.DB.batch([
    env.DB.prepare("INSERT INTO epoch_guards(operation_id,installation_id,expected_epoch,created_at) SELECT ?,?,?,? WHERE EXISTS(SELECT 1 FROM installations WHERE id=? AND epoch=? AND revoked_through_epoch=?)")
      .bind(op,i.id,expected,now,i.id,expected,floor),
    env.DB.prepare(`UPDATE buckets SET revision=revision+1,updated_at=? WHERE key IN (SELECT bucket_key FROM contributions WHERE id IN (${page})) AND ${guard}`)
      .bind(now,i.id,floor,op),
    // Permanent original-analysis receipts and all compatibility buckets stay.
    env.DB.prepare(`DELETE FROM contributions WHERE id IN (${page}) AND ${guard} RETURNING id`)
      .bind(i.id,floor,op),
    env.DB.prepare("DELETE FROM epoch_guards WHERE operation_id=?").bind(op),
    env.DB.prepare("SELECT epoch,EXISTS(SELECT 1 FROM contributions WHERE installation_id=? AND epoch<=?) AS cleanup_pending FROM installations WHERE id=? AND epoch=? AND revoked_through_epoch=?")
      .bind(i.id,floor,i.id,expected,floor)
  ]);
  if (!result[0]?.meta?.changes) throw new ApiError(409,"stale_epoch",{epoch:(await current(env,i.id)).epoch});
  const state=result[4]?.results?.[0];
  if (!state || typeof state.cleanup_pending !== "number") throw new ApiError(503,"cleanup_state_unavailable");
  const deleted=result[2]?.results?.length ?? 0;
  return {epoch:expected,withdrawn:deleted,cleanupDeleted:deleted,cleanupPending:state.cleanup_pending!==0};
}

async function cleanupWithdrawn(request: Request, env: Env, i: Installation, now: number) {
  const parameters=new URL(request.url).searchParams;
  parameters.forEach((_value,key)=>{if(key!=="epoch") throw new ApiError(400,"unexpected_fields");});
  const expected=withdrawalEpoch(request,i);
  return json(await cleanupWithdrawnPage(env,i,now,expected,i.revoked_through_epoch));
}

async function withdrawAll(env: Env, i: Installation, now: number, expected: number) {
  // One row CAS immediately withdraws every earlier contribution. A single
  // installation can grow to the native D1 storage limit without an unbounded
  // all-delete transaction or an arbitrary per-author upload cap.
  const changed=await env.DB.prepare("UPDATE installations SET revoked_through_epoch=MAX(revoked_through_epoch,epoch),epoch=epoch+1 WHERE id=? AND epoch=? RETURNING epoch,revoked_through_epoch")
    .bind(i.id,expected).first<{epoch:number;revoked_through_epoch:number}>();
  if (!changed) throw new ApiError(409,"stale_epoch",{epoch:(await current(env,i.id)).epoch});
  try {
    return json({...await cleanupWithdrawnPage(env,i,now,changed.epoch,changed.revoked_through_epoch),allWithdrawn:true});
  } catch {
    // The revocation barrier has already committed. Do not misreport the
    // withdrawal as failed or hide its new epoch when physical cleanup pauses.
    return json({epoch:changed.epoch,withdrawn:0,cleanupDeleted:0,cleanupPending:true,allWithdrawn:true});
  }
}

async function withdraw(request: Request, env: Env, i: Installation, now: number, id: string | null) {
  const expected=withdrawalEpoch(request,i);
  if (!id) return await withdrawAll(env,i,now,expected);
  textValue(id,/^[a-f0-9]{64}$/);
  const op = crypto.randomUUID();
  const idClause = " AND id=?";
  const selection = `SELECT bucket_key FROM contributions WHERE installation_id=?${idClause}`;
  const selectValues = [i.id,id];
  // D1 batch is one transaction. The guard is inserted only if CAS succeeded;
  // every subsequent mutation is guarded by this operation, not just epoch+1.
  const guard = "EXISTS(SELECT 1 FROM epoch_guards WHERE operation_id=?)";
  const statements = [
    env.DB.prepare("INSERT INTO epoch_guards(operation_id,installation_id,expected_epoch,created_at) SELECT ?,?,?,? WHERE EXISTS(SELECT 1 FROM installations WHERE id=? AND epoch=?)").bind(op,i.id,expected,now,i.id,expected),
    env.DB.prepare(`UPDATE installations SET epoch=epoch+1 WHERE id=? AND epoch=? AND ${guard}`).bind(i.id,expected,op),
    env.DB.prepare(`UPDATE buckets SET revision=revision+1,updated_at=? WHERE key IN (${selection}) AND ${guard}`).bind(now,...selectValues,op),
    // Epoch CAS + permanent original-analysis receipts already prevent replay.
    // Do not duplicate them into another ever-growing revocation log.
    env.DB.prepare(`DELETE FROM contributions WHERE installation_id=?${idClause} AND ${guard} RETURNING id`).bind(...selectValues,op),
    env.DB.prepare("DELETE FROM epoch_guards WHERE operation_id=?").bind(op)
  ];
  const result = await env.DB.batch(statements);
  if (!result[0]?.meta?.changes) throw new ApiError(409,"stale_epoch",{epoch:(await current(env,i.id)).epoch});
  // D1 meta.changes can include trigger side effects; RETURNING counts only the
  // actual withdrawn conclusions, not their capacity-counter updates.
  return json({epoch:expected+1,withdrawn:result[3]?.results?.length ?? 0});
}

async function feedback(request: Request, env: Env, i: Installation, now: number) {
  const data = object(await body(request),["epoch","key","reason"]); enabled(i,data.epoch);
  const key = textValue(data.key,/^[a-z0-9-]+\|[0-9]{1,4}\|[a-z0-9_:/-]+\|[a-z0-9-]+$/,MAX_FINGERPRINT_KEY_LENGTH);
  const [softwareId,versionMajor,roleId,shape]=key.split("|");
  if(!validFingerprint({softwareId,versionMajor,roleId,shape})) throw new ApiError(400,"unverified_role");
  const reason = textValue(data.reason,/^(wrong-owner|wrong-role|unsafe-advice|outdated)$/);
  await env.DB.prepare("INSERT INTO feedback(installation_id,bucket_key,reason,created_at) SELECT ?,?,?,? WHERE EXISTS(SELECT 1 FROM installations WHERE id=? AND epoch=? AND enabled=1) ON CONFLICT DO NOTHING").bind(i.id,key,reason,now,i.id,i.epoch).run();
  enabled(await current(env,i.id),i.epoch);
  // Reports are queued for review and never count as votes or disable all roles.
  return json({received:true,epoch:i.epoch});
}

export async function handle(request: Request, env: Env): Promise<Response> {
  try {
    const now = nowMs(), url = new URL(request.url);
    if (["/releases/latest.json", "/releases/history.json", "/downloads/manifest.json",
      "/v1/releases/latest.json", "/v1/releases/history.json", "/v1/releases/manifest.json"].includes(url.pathname))
      return await handleReleaseFeeds(request, env);
    if (!url.pathname.startsWith("/v1/")) throw new ApiError(404,"not_found");
    if (url.pathname === "/v1/announcements") return await handleAnnouncements(request, env);
    const known=(url.pathname==="/v1/rules"&&request.method==="GET") ||
      (url.pathname==="/v1/path-index"&&request.method==="GET") ||
      (url.pathname==="/v1/install"&&request.method==="POST") ||
      (url.pathname==="/v1/consent"&&["GET","PUT"].includes(request.method)) ||
      (url.pathname==="/v1/query"&&request.method==="POST") ||
      (url.pathname==="/v1/observations/query"&&request.method==="POST") ||
      (url.pathname==="/v1/contributions"&&["GET","POST","DELETE"].includes(request.method)) ||
      (url.pathname==="/v1/contributions/cleanup"&&request.method==="DELETE") ||
      (/^\/v1\/contributions\/[a-f0-9]{64}$/.test(url.pathname)&&request.method==="DELETE") ||
      (url.pathname==="/v1/feedback"&&request.method==="POST");
    if(!known) throw new ApiError(404,"not_found");
    if(!["enabled","read-only"].includes(env.SERVICE_MODE??"")) throw new ApiError(503,"service_paused");
    if(env.SERVICE_MODE==="read-only" && request.method==="POST" && ["/v1/install","/v1/contributions","/v1/feedback"].includes(url.pathname)) throw new ApiError(503,"new_data_paused");
    const day = await dailyLimit(request,env,now);
    // No retention deletion. Historical receipts prevent replay after
    // withdrawal; fixed counter slots reset their daily value in place.
    if (url.pathname === "/v1/rules" && request.method === "GET") return json({ruleVersion:RULE_VERSION,agreementVersion:AGREEMENT_VERSION,consensus:CONSENSUS,purposes:PURPOSES,actions:ACTIONS,rules:RULES,genericProtocol:"path2",purposeProtocol:"path3"});
    if (url.pathname === "/v1/path-index" && request.method === "GET") {
      url.searchParams.forEach((_value,key)=>{if(key!=="cursor") throw new ApiError(400,"unexpected_fields");});
      const cursor=url.searchParams.get("cursor") ?? "";
      if (url.searchParams.getAll("cursor").length>1 || cursor.length>512 || /[\u0000-\u001f]/.test(cursor))
        throw new ApiError(400,"invalid_path_index_cursor");
      return json(await createSharedPathIndex(env,url,now));
    }
    if (url.pathname === "/v1/install" && request.method === "POST") {
      object(await body(request),[]);
      const installationId = crypto.randomUUID(), managementToken = token();
      const created = await env.DB.prepare(`INSERT INTO installations(id,token_hash,created_at) SELECT ?,?,?
        WHERE EXISTS(SELECT 1 FROM operator_settings WHERE id=1 AND allow_new_contributors=1) RETURNING id`)
        .bind(installationId,await hash(managementToken),now).first();
      if (!created) throw new ApiError(503,"new_contributors_paused");
      return json({installationId,managementToken,epoch:0},201);
    }
    const i = await authenticate(request,env);
    if (url.pathname === "/v1/consent" && request.method === "GET") return json(consentResult(i));
    if (url.pathname === "/v1/consent" && request.method === "PUT") return await changeConsent(request,env,i,now);
    if (url.pathname === "/v1/query" && request.method === "POST") return await query(request,env,i,now);
    if (url.pathname === "/v1/observations/query" && request.method === "POST") return await queryOwnObservations(request,env,i,now);
    if (url.pathname === "/v1/contributions" && request.method === "POST") return await contribute(request,env,i,now,day);
    if (url.pathname === "/v1/contributions" && request.method === "GET") return await mine(request,env,i);
    if (url.pathname === "/v1/contributions" && request.method === "DELETE") return await withdraw(request,env,i,now,null);
    if (url.pathname === "/v1/contributions/cleanup" && request.method === "DELETE") return await cleanupWithdrawn(request,env,i,now);
    if (url.pathname.startsWith("/v1/contributions/") && request.method === "DELETE") return await withdraw(request,env,i,now,url.pathname.slice("/v1/contributions/".length));
    if (url.pathname === "/v1/feedback" && request.method === "POST") return await feedback(request,env,i,now);
    throw new ApiError(404,"not_found");
  } catch (e) {
    if(e instanceof Error && e.message.includes("shared_capacity_limit")) e=new ApiError(429,"storage_capacity_paused");
    if (e instanceof Error && /SQLITE_FULL|database or disk is full|exceeded (?:the )?maximum (?:database |storage )?size/i.test(e.message))
      e=new ApiError(429,"storage_capacity_paused");
    if (e instanceof Error && /exceeded.*(?:rows read|rows written|daily.*quota)|(?:rows read|rows written).*daily.*limit/i.test(e.message))
      e=new ApiError(429,"platform_daily_quota_paused");
    if (e instanceof ApiError) { const response = json({error:e.message,...e.extra},e.status); if (e.status === 429)
      response.headers.set("Retry-After",typeof e.extra.retryAfterSeconds === "number" ? String(e.extra.retryAfterSeconds) : "3600"); return response; }
    // Never echo SQL, request bodies, personal paths, credentials or raw errors.
    return json({error:"service_temporarily_unavailable"},503);
  }
}
// Retained as an explicit no-op for existing local test callers. The beta does
// not expire/delete stored records or register scheduled maintenance. Stored
// experience has no age cutoff; explicit owner withdrawal remains available.
export async function maintenance(_env: Env, _now = Date.now()): Promise<void> {}
export default {fetch:handle};
