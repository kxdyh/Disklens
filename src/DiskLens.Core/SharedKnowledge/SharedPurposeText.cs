using System.Text.RegularExpressions;
using System.Text;
using DiskLens.Core.Ai;

namespace DiskLens.Core.SharedKnowledge;

/// <summary>Validates purpose-only model text verbatim; never rewrites private text into a contribution.</summary>
public static class SharedPurposeText
{
    public const int MaximumShortPurposeLength = AiPurposeTextContract.MaximumShortPurposeLength;
    public const int MaximumReasonLength = AiPurposeTextContract.MaximumReasonLength;
    public const int MaximumShortPurposeEnLength = AiPurposeTextContract.MaximumShortPurposeEnLength;
    public const int MaximumReasonEnLength = AiPurposeTextContract.MaximumReasonEnLength;
    private static readonly HashSet<string> PublicPathNames = new(
        SharedFingerprintProtocol.PathTokens.Split(' ').Concat(
            ["Users", "AppData", "Local", "Roaming", "LocalLow", "Windows", "Microsoft", "ProgramData",
             "Program Files", "Program Files (x86)", "Documents and Settings", "Public", "Default", "All Users", "Default User"]),
        StringComparer.OrdinalIgnoreCase);

    public static bool IsKnownPublicPathName(string value) => PublicPathNames.Contains(value)
        || SharedPublicPurposeTerms.IsPublicPathName(value);

    public static bool IsValidPair(string? shortPurpose, string? reason)
    {
        if (shortPurpose is null || reason is null) return false;
        // Older clients only contributed controlled categories. Empty fields do not
        // pretend those rows contain a short purpose from the original model.
        if (string.IsNullOrEmpty(shortPurpose) && string.IsNullOrEmpty(reason)) return true;
        return IsValid(shortPurpose, MaximumShortPurposeLength) && IsValid(reason, MaximumReasonLength);
    }

    public static bool IsValidEnglishPair(string? shortPurposeEn, string? reasonEn)
    {
        if (shortPurposeEn is null || reasonEn is null) return false;
        // Absent translations keep old contributions and remote rows readable.
        if (shortPurposeEn.Length == 0 && reasonEn.Length == 0) return true;
        return IsValid(shortPurposeEn, MaximumShortPurposeEnLength, english: true)
            && IsValid(reasonEn, MaximumReasonEnLength, english: true);
    }

    public static bool ContainsPrivatePathContext(string shortPurpose, string reason, string localPath)
    {
        var parts = AiPurposeTextContract.CreateSensitiveScreeningText(localPath).Replace('/', '\\')
            .Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var text = AiPurposeTextContract.CreateSensitiveScreeningText(shortPurpose + " " + reason);
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!parts[i].Equals("Users", StringComparison.OrdinalIgnoreCase)
                && !parts[i].Equals("Documents and Settings", StringComparison.OrdinalIgnoreCase)) continue;
            var account = parts[i + 1];
            if (account is "Public" or "Default" or "All Users" or "Default User") continue;
            if (account.Length >= 2 && text.Contains(account, StringComparison.OrdinalIgnoreCase)) return true;
        }
        // A model may quote a private folder name without its separators. An
        // unknown local segment is therefore not allowed to leak through these
        // otherwise plain-purpose fields. Unresolved names keep free text
        // private; callers may retain controlled categories without rewriting AI text.
        foreach (var part in parts)
        {
            if (part.Length < 2 || part.EndsWith(':') || IsKnownPublicPathName(part)) continue;
            if (ContainsNonPublicOccurrence(text, part)) return true;
            var stem = Path.GetFileNameWithoutExtension(part);
            if (stem.Length >= 2 && stem != part && !IsKnownPublicPathName(stem)
                && ContainsNonPublicOccurrence(text, stem)) return true;
        }
        return false;
    }

    private static bool ContainsNonPublicOccurrence(string text, string segment)
    {
        static bool IsAsciiLetter(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
        for (var offset = 0; offset <= text.Length - segment.Length;)
        {
            var found = text.IndexOf(segment, offset, StringComparison.OrdinalIgnoreCase);
            if (found < 0) return false;
            var end = found + segment.Length;
            // Only a complete, reviewed ordinary English word can explain away
            // an embedded substring. Unknown compound names remain private.
            if (!segment.All(IsAsciiLetter)) return true;
            var start = found;
            while (start > 0 && IsAsciiLetter(text[start - 1])) start--;
            var wordEnd = end;
            while (wordEnd < text.Length && IsAsciiLetter(text[wordEnd])) wordEnd++;
            if (wordEnd - start <= segment.Length
                || !SharedPublicPurposeTerms.IsPublicEnglishPurposeWord(text[start..wordEnd])) return true;
            offset = end;
        }
        return false;
    }

    private static bool IsValid(string? value, int max, bool english = false)
    {
        if (string.IsNullOrWhiteSpace(value) || value.EnumerateRunes().Count() > max || value != value.Trim()) return false;
        var allowed = english ? @"^[A-Za-z0-9 (),.;:+&'\-\u00A0\u202F\u2010-\u2015\u2018-\u201F\u2026]+$"
            : @"^[\p{IsCJKUnifiedIdeographs}A-Za-z0-9 ()（），。、；：+\-]+$";
        if (!Regex.IsMatch(value, allowed)) return false;
        var screeningText = english ? AiPurposeTextContract.CreateSensitiveScreeningText(value) : value;
        return (!english || !Regex.IsMatch(screeningText, @"(?i)\b(?:username|email|phone|credential)s?\b"))
            && !Regex.IsMatch(screeningText,
            @"(?i)(?:api[ -]*key|sk[-_]|bearer|password|https?|[a-z]盘|\d{7,}|密码|密钥|用户名|邮箱|电话|身份证|个人姓名)");
    }
}
