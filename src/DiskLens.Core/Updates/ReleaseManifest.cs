using System.Text.Json;
using System.Text.RegularExpressions;

namespace DiskLens.Core.Updates;

public sealed record ReleaseManifest(Version Version, DateOnly PublishedAt, Uri DownloadPage, IReadOnlyList<string> Notes)
{
    public static readonly Uri FeedUri = new("https://disklens.site/releases/latest.json");
    public static readonly Uri OfficialDownloadPage = new("https://disklens.site/#download");
    public string VersionText => Version.ToString(3);
    // Empty for legacy feeds; callers choose an explicit untranslated fallback.
    public IReadOnlyList<string> NotesEn { get; init; } = [];

    public static bool TryRead(ReadOnlyMemory<byte> json, out ReleaseManifest? manifest)
    {
        manifest = null;
        if (json.Length is 0 or > 65_536) return false;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number
                || !schema.TryGetInt32(out var schemaVersion) || schemaVersion != 1
                || !TryGetString(root, "version", out var versionText) || !TryParseVersion(versionText, out var version)
                || !TryGetString(root, "publishedAt", out var dateText)
                || !DateOnly.TryParseExact(dateText, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var publishedAt)
                || !TryGetString(root, "downloadPage", out var page) || !IsOfficialDownloadPage(page)
                || !root.TryGetProperty("notes", out var notes) || notes.ValueKind != JsonValueKind.Array
                || notes.GetArrayLength() is < 1 or > 12) return false;
            var lines = new List<string>();
            foreach (var item in notes.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } text
                    || string.IsNullOrWhiteSpace(text) || text.Length > 500 || text.Any(char.IsControl)) return false;
                lines.Add(text.Trim());
            }
            var englishLines = new List<string>();
            if (root.TryGetProperty("notesEn", out var notesEn))
            {
                if (notesEn.ValueKind != JsonValueKind.Array || notesEn.GetArrayLength() is < 1 or > 12) return false;
                foreach (var item in notesEn.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } text
                        || string.IsNullOrWhiteSpace(text) || text.Length > 500 || text.Any(char.IsControl)) return false;
                    englishLines.Add(text.Trim());
                }
            }
            // Links in a remote feed never control what is executed or which site is opened.
            manifest = new ReleaseManifest(version!, publishedAt, OfficialDownloadPage, lines) { NotesEn = englishLines };
            return true;
        }
        // JsonDocument may parse an unpaired escaped surrogate, then GetString fails
        // while decoding it. Treat that corruption exactly like invalid JSON.
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return false; }
    }

    public static bool TryParseVersion(string? text, out Version? version)
    {
        version = null;
        return text is { Length: <= 32 } && Regex.IsMatch(text, @"^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) && Version.TryParse(text, out version);
    }

    private static bool TryGetString(JsonElement root, string name, out string? value)
    {
        value = null;
        if (!root.TryGetProperty(name, out var item) || item.ValueKind != JsonValueKind.String) return false;
        value = item.GetString();
        return value is not null;
    }

    private static bool IsOfficialDownloadPage(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo)
        && string.Equals(uri.Host, "disklens.site", StringComparison.OrdinalIgnoreCase)
        && uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query) && uri.Fragment is "" or "#download";
}

public static class ReleaseUpdatePolicy
{
    public static bool ShouldOffer(ReleaseManifest release, Version installedVersion, string? ignoredVersion, bool manual = false) =>
        release.Version > installedVersion && (manual || !string.Equals(release.VersionText, ignoredVersion, StringComparison.Ordinal));
}
