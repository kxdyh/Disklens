export const AGREEMENT_VERSION = "2026-11-beta3";
export const RULE_VERSION = 3;
export const CONSENSUS = { minimumInstallations: 1, minimumAgeHours: 0, observationHours: 0, agreementRatio: 0 };
export type Fingerprint = { softwareId: string; versionMajor: string; roleId: string; shape: string };
export type Rule = {
  softwareId: string; softwareLabel: string; roleId: string; roleLabel: string;
  shape: string; purpose: string; actions: string[]; conditions: string[]; minVersionMajor: number; maxVersionMajor: number; reusable?: boolean;
};
export const PURPOSES = ["cache", "runtime", "application", "configuration", "logs", "downloads", "user-data", "unknown"] as const;
export const ACTIONS = ["Review", "Keep", "DoNotDelete", "RecycleCandidate"] as const;
// An explicit advisory action is preserved; legacy Review never implies it.
// Other bounded contrary advice remains storable so disagreement is not hidden.
export function validContributionAction(purpose: string, action: string): boolean {
  return (ACTIONS as readonly string[]).includes(action)
    && (action !== "RecycleCandidate" || ["cache", "logs", "downloads"].includes(purpose));
}
export const CONFIDENCE_LEVELS = ["High", "Medium", "Low", "Unknown"] as const;
export type ConfidenceLevel = typeof CONFIDENCE_LEVELS[number];
// These are public category tokens, never arbitrary filenames or path segments.
export const PATH_TOKENS = new Set(("volume user-downloads user-documents user-desktop user-pictures user-videos user-music user-local user-roaming user-temp root mask " +
  "cache caches temp tmp logs log crash crashes crashdumps reports backup backups downloads download updates update packages package data userdata user-data profiles profile config configuration settings preferences storage localstorage databases database db history cookies sessions session " +
  "bin bin64 lib lib64 libexec runtime runtimes resources assets plugins extensions drivers include licenses license qemu emulator platform-tools build-tools platforms system-images tools cmdline-tools sdk ndk gradle build builds dist target obj node_modules vendor site-packages models weights checkpoints media music videos pictures documents projects project workspace workspaces src source tests test output outputs results").split(" "));
export const FILE_EXTENSIONS = new Set(("tmp temp log cache part download lock exe dll so dylib sys msi msix zip 7z rar tar gz iso img vhd vhdx mp4 mkv mov avi mp3 wav flac jpg jpeg png gif webp pdf docx xlsx pptx txt json xml ini yaml yml toml db sqlite bin dat pak py js ts cs cpp h java onnx pt safetensors gguf other none").split(" "));
// Mirrored in SharedFingerprintProtocol: actual OS anchors are verified locally.
export const STANDARD_PURPOSE_ROOT_TOKENS = ("os-windows os-windows-system32 os-windows-syswow64 os-windows-winsxs " +
  "os-windows-assembly os-windows-fonts os-windows-installer os-windows-dotnet os-windows-systemapps " +
  "os-windows-softwaredistribution os-windows-systemresources os-windows-serviceprofiles " +
  "os-windows-speech-onecore os-windows-speech os-windows-boot os-windows-prefetch os-windows-uus " +
  "os-windows-cursors os-windows-apppatch os-windows-rescache os-windows-inf os-windows-inputmethod " +
  "os-windows-panther os-windows-globalization os-windows-servicing os-windows-appcompat os-windows-web " +
  "os-windows-containers os-windows-ime os-windows-media os-windows-immersivecontrolpanel " +
  "os-windows-shellexperiences os-windows-logs os-windows-inboxapps os-windows-diagnostics " +
  "os-windows-provisioning os-windows-shellcomponents os-windows-ocr os-windows-diagtrack " +
  "os-windows-security os-windows-pla os-windows-printdialog os-windows-help os-windows-resources " +
  "os-profile-tool-cache os-profile-tool-nuget os-profile-tool-gradle os-profile-tool-codex " +
  "os-profile-tool-vscode os-profile-tool-docker os-profile-tool-cargo os-profile-tool-rustup " +
  "os-profile-tool-npm os-profile-tool-m2 os-profile-tool-p2 os-profile-tool-virtualenvs " +
  "os-profile-tool-insightface os-profile-tool-android os-profile-tool-claude os-profile-tool-cursor").split(" ");
export const PURPOSE_ROOT_TOKENS = new Set([
  ..."os-users os-profile os-appdata os-local os-roaming os-locallow os-temp os-programdata os-programfiles os-programfiles-x86 os-downloads os-documents os-desktop os-pictures os-videos os-music".split(" "),
  ...STANDARD_PURPOSE_ROOT_TOKENS
]);
export const MAX_ROLE_LENGTH = 256;
export const MAX_FINGERPRINT_KEY_LENGTH = 512;
// Legacy roles remain readable, without pretending old records have confidence.
export const RULES: Rule[] = [
  { softwareId: "android-sdk", softwareLabel: "Android SDK", roleId: "sdk-temp", roleLabel: "SDK 临时缓存", shape: "cache-only-v1", purpose: "cache", actions: ["Review", "RecycleCandidate"], conditions: ["本机已验证 Android SDK 安装根与目录角色。", "仅含已识别临时缓存；没有用户文件、链接或未读内容。", "确认没有安装、下载或构建任务运行后，再自行判断是否清理。"], minVersionMajor: 0, maxVersionMajor: 99 },
  { softwareId: "android-sdk", softwareLabel: "Android SDK", roleId: "sdk-emulator", roleLabel: "模拟器运行依赖", shape: "runtime-v1", purpose: "runtime", actions: ["Keep", "DoNotDelete"], conditions: ["本机已验证 SDK 软件归属、模拟器版本及运行时结构。", "这是模拟器运行依赖；手动删除可能导致模拟器无法启动。", "不再需要时，请通过 SDK 管理器卸载对应组件。"], minVersionMajor: 0, maxVersionMajor: 99 },
  { softwareId: "wegame", softwareLabel: "WeGame", roleId: "wegame-temp", roleLabel: "WeGame 临时缓存", shape: "cache-only-v1", purpose: "cache", actions: ["Review", "RecycleCandidate"], conditions: ["本机已验证 WeGame 安装根与临时目录角色。", "目录中没有用户数据、链接或未读内容。", "关闭客户端并确认没有下载或更新任务后，再自行判断是否清理。"], minVersionMajor: 0, maxVersionMajor: 9999 }
];
export function fingerprintKey(f: Fingerprint): string { return `${f.softwareId}|${f.versionMajor}|${f.roleId}|${f.shape}`; }
export function isV2Fingerprint(f: Fingerprint): boolean {
  if (!(f.softwareId === "generic-v2" || /^app-[a-f0-9]{64}$/.test(f.softwareId))
    || !/^[0-9]{1,4}$/.test(f.versionMajor) || (f.softwareId === "generic-v2" && f.versionMajor !== "0")
    || !/^(tree2|partial2)-[a-f0-9]{64}$/.test(f.shape) || !f.roleId.startsWith("path2:") || f.roleId.length > MAX_ROLE_LENGTH) return false;
  const segments = f.roleId.slice(6).split("/");
  return segments.length >= 1 && segments.length <= 12 && (!segments.includes("root") || segments.length === 1)
    && segments.every((segment, index) => PATH_TOKENS.has(segment)
      || index === segments.length - 1 && segment.startsWith("file-") && FILE_EXTENSIONS.has(segment.slice(5)));
}
export function isPurposePathToken(value: string): boolean {
  return PATH_TOKENS.has(value) && !["volume", "root", "mask"].includes(value) && !value.startsWith("user-");
}
export function isPathPurposeFingerprint(f: Fingerprint): boolean {
  const observation=/^purpose3-observation-(directory|file)-[a-f0-9]{32}$/.exec(f.shape);
  const observationKind=observation&&observation[0]===f.shape?observation[1]:null;
  const shared=/^purpose3-shared-(directory|file)-[a-f0-9]{64}$/.exec(f.shape);
  const sharedKind=shared&&shared[0]===f.shape?shared[1]:null;
  const isDirectory=f.shape==="purpose3-directory"||observationKind==="directory"||sharedKind==="directory";
  const isFile=f.shape==="purpose3-file"||observationKind==="file"||sharedKind==="file";
  if (!/^[0-9]{1,4}$/.test(f.versionMajor) || (!isDirectory&&!isFile)
    || !f.roleId.startsWith("path3:") || f.roleId.length > MAX_ROLE_LENGTH) return false;
  const segments = f.roleId.slice(6).split("/");
  if (segments.length < 1 || segments.length > 12) return false;
  const validTail=(tail:string[])=>tail.every((part,index)=>isPurposePathToken(part)||part==="mask"
    || isFile&&index===tail.length-1&&part.startsWith("file-")&&FILE_EXTENSIONS.has(part.slice(5)));
  if (isFile&&(!segments.at(-1)!.startsWith("file-")||!FILE_EXTENSIONS.has(segments.at(-1)!.slice(5)))) return false;
  if (f.softwareId === "generic-v3") return f.versionMajor === "0" && (PURPOSE_ROOT_TOKENS.has(segments[0])||segments[0]==="volume")
    && validTail(segments.slice(1));
  return /^app-[a-f0-9]{64}$/.test(f.softwareId)
    && (segments.length === 1 && segments[0] === "root" && isDirectory || validTail(segments));
}
export function reusableFingerprint(f: Fingerprint): boolean {
  // Opaque path identities are installation-local observations, never shared
  // identities or independent evidence for a reusable cross-device purpose.
  if (isPathPurposeFingerprint(f)) return /^purpose3-shared-(directory|file)-[a-f0-9]{64}$/.test(f.shape)
    || f.shape==="purpose3-directory" && !f.roleId.slice(6).split('/').some(part=>['mask','volume'].includes(part));
  if (!isV2Fingerprint(f)) return false;
  const segments = f.roleId.slice(6).split("/");
  if (!f.shape.startsWith("tree2-") || segments.some(segment => ["mask", "file-other", "file-none"].includes(segment))) return false;
  // Generic files need public context beyond a volume or file extension alone.
  return f.softwareId !== "generic-v2" || !segments.some(segment => segment.startsWith("file-"))
    && segments.some(segment => !["volume", "root"].includes(segment));
}
export function ruleFor(f: Fingerprint): Rule | undefined {
  // Public OS anchors are already protected by the desktop location policy.
  // Anonymous cloud advice must not turn those roots into deletion exceptions.
  const root = f.roleId.startsWith("path3:") ? f.roleId.slice(6).split('/')[0] : "";
  const sharedPath = /^purpose3-shared-(directory|file)-[a-f0-9]{64}$/.test(f.shape);
  const protectedAnchor = isPathPurposeFingerprint(f)
    && (["os-programdata", "os-programfiles", "os-programfiles-x86", "os-windows"].includes(root)
      || root.startsWith("os-windows-"));
  if (isPathPurposeFingerprint(f)) return { softwareId: f.softwareId, softwareLabel: sharedPath ? "共享路径" : f.softwareId === "generic-v3" ? "系统公共路径角色" : "已验证软件",
    roleId: f.roleId, roleLabel: f.roleId, shape: f.shape, purpose: "*", actions: protectedAnchor ? ["Review", "Keep", "DoNotDelete"] : [...ACTIONS],
    conditions: [sharedPath ? "归一化路径标识相同，采用已上传的中高置信度分析结果。" : "本机已核验系统路径角色或注册软件公共产品身份与受控内部路径。", "经验仅解释本项路径用途；没有核验子树内容、读取完整性或删除安全。", "中高置信度用途结论不构成删除许可；清理前仍需人工核验本机内容与使用情况。"],
    minVersionMajor: 0, maxVersionMajor: 9999, reusable: reusableFingerprint(f) };
  if (isV2Fingerprint(f)) return { softwareId: f.softwareId, softwareLabel: f.softwareId === "generic-v2" ? "通用文件" : "已验证软件",
    roleId: f.roleId, roleLabel: f.roleId, shape: f.shape, purpose: "*", actions: [...ACTIONS],
    conditions: ["本机已核验匿名公共路径类别与文件结构相同。", "经验来自中高置信度的 AI 分析，仍需结合本机用途判断。", "Review 只表示可人工复核；确认没有使用、下载或更新任务后，再决定是否清理。"],
    minVersionMajor: 0, maxVersionMajor: 9999, reusable: reusableFingerprint(f) };
  return RULES.find(r => r.softwareId === f.softwareId && r.roleId === f.roleId && r.shape === f.shape);
}
export function validFingerprint(f: Fingerprint): boolean {
  const rule = ruleFor(f);
  const major = Number(f.versionMajor);
  return /^[0-9]{1,4}$/.test(f.versionMajor) && !!rule && major >= rule.minVersionMajor && major <= rule.maxVersionMajor;
}
export function validatedConclusion(rule: Rule, purpose: string, action: string): boolean {
  if (!validContributionAction(purpose, action)) return false;
  if (rule.purpose === "*") return rule.reusable === true && (PURPOSES as readonly string[]).includes(purpose)
    && purpose !== "unknown" && rule.actions.includes(action) && !(["runtime", "user-data"].includes(purpose) && action === "Review");
  return purpose === rule.purpose && rule.actions.includes(action);
}
export type ConsensusEvidence = {purpose: string; action: string; first_observed: number; created_at: number; provider_class: string; default_prompt: number; shared_context: number; confidence_level?: string};
// Both the decision and its original-text exemplar must use this exact boundary.
export function qualifiedEvidence(row: ConsensusEvidence, now: number): boolean {
  // An accepted medium/high analysis is immediately reusable. Provider and
  // prompt declarations are retained as provenance, not publication gates.
  return ["High", "Medium"].includes(row.confidence_level ?? "Unknown");
}
export function consensusDecision(rows: ConsensusEvidence[], now: number) {
  const eligible = rows.filter(r => qualifiedEvidence(r, now));
  const selected = eligible.sort((a,b) => Number(b.confidence_level === "High")-Number(a.confidence_level === "High")
    || b.first_observed-a.first_observed || a.purpose.localeCompare(b.purpose) || a.action.localeCompare(b.action))[0];
  return selected ? {purpose:selected.purpose,action:selected.action,observations:eligible.length,
    confidenceLevel:selected.confidence_level!} : null;
}
export function explanationFor(purpose: string, action: string): string {
  const labels: Record<string,string> = {cache:"缓存",runtime:"运行依赖",application:"应用文件",configuration:"配置",logs:"日志",downloads:"下载内容", "user-data":"用户数据",unknown:"未知用途"};
  const prefix = `云端经验将此类结构识别为${labels[purpose] ?? labels.unknown}。`;
  return prefix + (action === "RecycleCandidate" ? "可以考虑清理；仍需核对本机内容、使用状态及本地保护规则。" : action === "Review" ? "请核对本机用途并手动确认后，再决定是否清理。" : action === "DoNotDelete" ? "建议不要删除，仍需结合本机使用情况核验。" : "建议保留，仍需结合本机使用情况核验。");
}
