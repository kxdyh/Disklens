using System.Text.RegularExpressions;

namespace DiskLens.Core.SharedKnowledge;

/// <summary>A privacy vocabulary, not a list of supported applications. Arbitrary names become mask.</summary>
public static class SharedFingerprintProtocol
{
    // Recovering the current installation's exact random observation is separate
    // from claiming that an anonymous category has the same purpose elsewhere.
    public static bool IsOwnObservation(SharedFingerprint fingerprint) => IsValid(fingerprint)
        && fingerprint.Shape.StartsWith("purpose3-observation-", StringComparison.Ordinal);
    public static bool IsSharedPath(SharedFingerprint fingerprint) => IsValid(fingerprint)
        && Regex.IsMatch(fingerprint.Shape, "\\Apurpose3-shared-(directory|file)-[a-f0-9]{64}\\z");
    public const string PathTokens = "volume user-downloads user-documents user-desktop user-pictures user-videos user-music user-local user-roaming user-temp root mask cache caches temp tmp logs log crash crashes crashdumps reports backup backups downloads download updates update packages package data userdata user-data profiles profile config configuration settings preferences storage localstorage databases database db history cookies sessions session bin bin64 lib lib64 libexec runtime runtimes resources assets plugins extensions drivers include licenses license qemu emulator platform-tools build-tools platforms system-images tools cmdline-tools sdk ndk gradle build builds dist target obj node_modules vendor site-packages models weights checkpoints media music videos pictures documents projects project workspace workspaces src source tests test output outputs results";
    public const string ExtensionTokens = "tmp temp log cache part download lock exe dll so dylib sys msi msix zip 7z rar tar gz iso img vhd vhdx mp4 mkv mov avi mp3 wav flac jpg jpeg png gif webp pdf docx xlsx pptx txt json xml ini yaml yml toml db sqlite bin dat pak py js ts cs cpp h java onnx pt safetensors gguf other none";
    // A separate domain for path purpose, never a claim about contents or deletion safety.
    public const string PurposeRootTokens = "os-users os-profile os-appdata os-local os-roaming os-locallow os-temp os-programdata os-programfiles os-programfiles-x86 os-downloads os-documents os-desktop os-pictures os-videos os-music "
        + SharedStandardPurposeCatalog.RoleTokens;
    private static readonly HashSet<string> Paths = new(PathTokens.Split(' '), StringComparer.Ordinal);
    private static readonly HashSet<string> Extensions = new(ExtensionTokens.Split(' '), StringComparer.Ordinal);
    private static readonly HashSet<string> PurposeRoots = new(PurposeRootTokens.Split(' '), StringComparer.Ordinal);
    private static readonly HashSet<string> StandardPurposeRoots = new(SharedStandardPurposeCatalog.RoleTokens.Split(' '), StringComparer.Ordinal);
    /// <summary>Exact new standard-directory roots; descendants are not maintenance migration targets.</summary>
    public static bool IsStandardPurposeRootRole(string roleId) => roleId.StartsWith("path3:", StringComparison.Ordinal)
        && StandardPurposeRoots.Contains(roleId[6..]);
    public static readonly string[] Purposes = ["cache", "runtime", "application", "configuration", "logs", "downloads", "user-data", "unknown"];
    public static bool IsPathToken(string value) => Paths.Contains(value);
    public static string ExtensionToken(string extension)
    {
        var value = extension.TrimStart('.').ToLowerInvariant();
        return value.Length == 0 ? "none" : Extensions.Contains(value) ? value : "other";
    }
    public static bool IsV2(SharedFingerprint f) => f.RoleId?.StartsWith("path2:", StringComparison.Ordinal) == true;
    public static bool IsPathPurpose(SharedFingerprint f) => f.RoleId?.StartsWith("path3:", StringComparison.Ordinal) == true;
    public static bool IsPurposePathToken(string value) => Paths.Contains(value)
        && value is not ("volume" or "root" or "mask") && !value.StartsWith("user-", StringComparison.Ordinal);
    private static bool ValidPathPurpose(SharedFingerprint f)
    {
        var isDirectory = f.Shape == "purpose3-directory"
            || Regex.IsMatch(f.Shape ?? "", "\\Apurpose3-observation-directory-[a-f0-9]{32}\\z")
            || Regex.IsMatch(f.Shape ?? "", "\\Apurpose3-shared-directory-[a-f0-9]{64}\\z");
        var isFile = f.Shape == "purpose3-file"
            || Regex.IsMatch(f.Shape ?? "", "\\Apurpose3-observation-file-[a-f0-9]{32}\\z")
            || Regex.IsMatch(f.Shape ?? "", "\\Apurpose3-shared-file-[a-f0-9]{64}\\z");
        if ((!isDirectory && !isFile) || f.RoleId.Length > 256) return false;
        var parts = f.RoleId[6..].Split('/');
        if (parts.Length is < 1 or > 12) return false;
        bool ValidTail(IEnumerable<string> tail)
        {
            var values = tail.ToArray();
            return values.Select((part, i) => IsPurposePathToken(part) || part == "mask"
                || isFile && i == values.Length - 1 && part.StartsWith("file-", StringComparison.Ordinal)
                    && Extensions.Contains(part[5..])).All(x => x);
        }
        if (isFile && (!parts[^1].StartsWith("file-", StringComparison.Ordinal)
            || !Extensions.Contains(parts[^1][5..]))) return false;
        if (f.SoftwareId == "generic-v3") return f.VersionMajor == "0" && (PurposeRoots.Contains(parts[0]) || parts[0] == "volume")
            && ValidTail(parts.Skip(1));
        return Regex.IsMatch(f.SoftwareId ?? "", "^app-[a-f0-9]{64}$")
            && (parts.Length == 1 && parts[0] == "root" && isDirectory || ValidTail(parts));
    }
    public static bool IsValid(SharedFingerprint f)
    {
        if (!Regex.IsMatch(f.VersionMajor ?? "", "^[0-9]{1,4}$")) return false;
        if (IsPathPurpose(f)) return ValidPathPurpose(f);
        if (!IsV2(f)) return f.SoftwareId == "android-sdk" && (f.RoleId == "sdk-temp" && f.Shape == "cache-only-v1"
                || f.RoleId == "sdk-emulator" && f.Shape == "runtime-v1")
            || f.SoftwareId == "wegame" && f.RoleId == "wegame-temp" && f.Shape == "cache-only-v1";
        if (!(f.SoftwareId == "generic-v2" && f.VersionMajor == "0"
            || Regex.IsMatch(f.SoftwareId ?? "", "^app-[a-f0-9]{64}$"))
            || !Regex.IsMatch(f.Shape ?? "", "^(tree2|partial2)-[a-f0-9]{64}$")) return false;
        var segments = f.RoleId[6..].Split('/');
        return segments.Length is >= 1 and <= 12 && segments.Select((s, i) =>
            Paths.Contains(s) || i == segments.Length - 1 && s.StartsWith("file-", StringComparison.Ordinal) && Extensions.Contains(s[5..])).All(x => x)
            && (!segments.Contains("root") || segments.Length == 1);
    }
    public static bool CanReuse(SharedFingerprint f)
    {
        if (!IsValid(f)) return false;
        // The full normalized path digest distinguishes masked names across
        // installations; historical random observations are never public keys.
        if (IsPathPurpose(f)) return IsSharedPath(f) || f.Shape == "purpose3-directory"
            && !f.RoleId[6..].Split('/').Any(x => x is "mask" or "volume");
        if (!IsV2(f)) return true;
        var segments = f.RoleId[6..].Split('/');
        if (!f.Shape.StartsWith("tree2-", StringComparison.Ordinal) || segments.Contains("mask")
            || segments.Contains("file-other") || segments.Contains("file-none")) return false;
        if (f.SoftwareId != "generic-v2") return true;
        // Extension alone cannot identify an arbitrary private file's purpose.
        if (segments.Any(x => x.StartsWith("file-", StringComparison.Ordinal))) return false;
        return segments.Any(x => x is not ("volume" or "root") && !x.StartsWith("file-", StringComparison.Ordinal));
    }
    public static bool ValidConclusion(string purpose, string action) => Purposes.Contains(purpose)
        && (action == "RecycleCandidate" ? purpose is "cache" or "logs" or "downloads"
            : action is "Review" or "Keep" or "DoNotDelete" && !(purpose == "runtime" && action == "Review"));
}
