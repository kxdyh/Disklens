// Mirrors SharedPurposeText. Validated original model text is stored unchanged.
// It is not a place for local paths, personal identifiers, credentials or markup.
export function validPurposeTextPair(shortPurpose: unknown, reason: unknown): boolean {
  if (typeof shortPurpose !== "string" || typeof reason !== "string") return false;
  if (shortPurpose === "" && reason === "") return true; // Legacy category-only evidence.
  const valid = (text: string, maximum: number) => text.length > 0 && text.length <= maximum
    && text.trim() === text && /^[\u4e00-\u9fffA-Za-z0-9 ()（），。、；：+\-]+$/.test(text)
    && !/(?:api[ -]*key|sk[-_]|bearer|password|https?|[a-z]盘|\d{7,}|密码|密钥|用户名|邮箱|电话|身份证|个人姓名)/i.test(text);
  return valid(shortPurpose, 12) && valid(reason, 30);
}

// English translations are a second complete pair, never new consensus evidence.
// Empty defaults keep older clients and already-stored observations readable.
// Mirror SharedPurposeText: normal English typography is preserved verbatim;
// controls, other scripts, emoji, paths and credential guards remain excluded.
export function validEnglishPurposeTextPair(shortPurpose: unknown, reason: unknown): boolean {
  if (typeof shortPurpose !== "string" || typeof reason !== "string") return false;
  if (shortPurpose === "" && reason === "") return true;
  const valid = (text: string, maximum: number) => {
    // Screen typographic variants too, without changing the original answer.
    const screening = text.replace(/[\u00a0\u202f]/gu," ").replace(/[\u2010-\u2015]/gu,"-");
    return text.length > 0 && [...text].length <= maximum
      && text.trim() === text && /^[A-Za-z0-9 (),.;:+&'\-\u00a0\u202f\u2010-\u2015\u2018-\u201f\u2026]+$/u.test(text)
      && !/\b(?:username|email|phone|credential)s?\b/i.test(screening)
      && !/(?:api[ -]*key|sk[-_]|bearer|password|https?|\d{7,})/i.test(screening);
  };
  return valid(shortPurpose, 80) && valid(reason, 240);
}
