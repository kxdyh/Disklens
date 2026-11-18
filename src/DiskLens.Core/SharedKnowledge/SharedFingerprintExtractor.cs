using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DiskLens.Core.Models;

namespace DiskLens.Core.SharedKnowledge;

/// <summary>Public matching uses stable normalized identities; raw local paths stay on the device.</summary>
public sealed class SharedFingerprintExtractor(ISoftwareIdentityResolver identityResolver,
    ISharedObservationIdentityProvider? observationIdentityProvider = null) : ISharedFingerprintExtractor
{
    private static readonly Lazy<PurposeRootIndex> PurposeRoots = new(() => new(CreatePurposeRoots(false)));
    private static readonly Lazy<PurposeRootIndex> LegacyPurposeRoots = new(() => new(CreatePurposeRoots(true)));
    private bool _isLookupSnapshot;
    private readonly Dictionary<string, bool> _lookupAncestorSafety = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string>? _lookupExclusions;
    private LookupPathNode? _lookupExclusionRoot;

    /// <summary>Build a per-lookup extractor that can only restore existing private identities.</summary>
    public async Task<SharedFingerprintExtractor> CreateLookupExtractorAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_isLookupSnapshot) return this;
        var snapshot = observationIdentityProvider is null ? EmptySharedObservationLookupSnapshot.Instance
            : await observationIdentityProvider.CreateLookupSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var softwareSnapshot = await identityResolver.CreateLookupSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return new SharedFingerprintExtractor(softwareSnapshot, snapshot) { _isLookupSnapshot = true };
    }

    public bool CanRestoreOwnObservationPath(string localPath) => ValidLocalPath(localPath)
        && observationIdentityProvider is ISharedObservationLookupSnapshot snapshot
        && snapshot.ContainsPath(Normalize(localPath));

    /// <summary>Cheap public-role prefilter using the process's actual OS locations, never guessed user paths.</summary>
    public static bool HasPotentialPublicPurposePath(string localPath)
    {
        if (!ValidLocalPath(localPath)) return false;
        var parts = KnownPurposeParts(Normalize(localPath));
        return parts is { Count: <= 12 } && SharedFingerprintProtocol.CanReuse(
            new SharedFingerprint("generic-v3", "0", "path3:" + string.Join('/', parts), "purpose3-directory"));
    }

    /// <summary>Cheap cross-installation key; no registry, file reads, or random installation identity.</summary>
    public static SharedFingerprint? CreateSharedPathFingerprint(string localPath, bool isDirectory)
    {
        if (!ValidLocalPath(localPath)) return null;
        var path = Normalize(localPath);
        if (path == "<invalid-path>") return null;
        return CreateSharedPathFingerprintNormalized(path, isDirectory);
    }

    private static SharedFingerprint? CreateSharedPathFingerprintNormalized(string path, bool isDirectory)
    {
        var fullParts = KnownPurposeParts(path) ?? ["volume", .. RelativeParts(path, path[..3])];
        if (fullParts.Count > 1 && fullParts[0] == "os-users")
            fullParts = ["os-profile", .. fullParts.Skip(2)];
        // Hash the complete path before bounding the display role or replacing
        // private segments. Different masked directories must not share a key.
        var kind = isDirectory ? "directory" : "file";
        var identity = "public-normalized-path-v1\n" + kind + "\n"
            + string.Join('/', fullParts.Select(part => part.Normalize(NormalizationForm.FormC)));
        var parts = fullParts.Count > 12 ? new List<string>([.. fullParts.Take(10), "mask", fullParts[^1]])
            : new List<string>(fullParts);
        if (!isDirectory)
        {
            var fileRole = "file-" + SharedFingerprintProtocol.ExtensionToken(Path.GetExtension(path));
            if (parts.Count == 0) parts.Add(fileRole); else parts[^1] = fileRole;
        }
        var tokens = parts.Select((part, index) => index == 0
            || SharedFingerprintProtocol.IsPurposePathToken(part)
            || !isDirectory && index == parts.Count - 1 && part.StartsWith("file-", StringComparison.Ordinal)
                ? part : "mask");
        var fingerprint = new SharedFingerprint("generic-v3", "0", "path3:" + string.Join('/', tokens),
            $"purpose3-shared-{kind}-{Digest(identity)}");
        return SharedFingerprintProtocol.IsValid(fingerprint) ? fingerprint : null;
    }

    public Task<SharedFingerprint?> ExtractAsync(AiCandidateRecord candidate, IReadOnlyList<string> exclusions,
        CancellationToken cancellationToken = default)
        => ExtractCoreAsync(candidate, exclusions, legacyOwnObservation: false, cancellationToken);

    /// <summary>Checks every path in memory before doing live checks for an indexed match.</summary>
    public async Task<(SharedFingerprint? Fingerprint, bool IsIndexMiss)> ExtractForLookupAsync(
        AiCandidateRecord candidate, IReadOnlyList<string> exclusions, SharedPathIndex index,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_isLookupSnapshot || !index.IsComplete
            || candidate.AnalysisEvidenceScope != AiAnalysisEvidenceScope.PathPurpose)
            return (await ExtractAsync(candidate, exclusions, cancellationToken).ConfigureAwait(false), false);
        if (!ValidLocalPath(candidate.Path) || candidate.HasLimitedAnalysisEvidence
            || candidate.Structure.Attributes.HasFlag(FileAttributes.ReparsePoint)
            || candidate.Structure.RiskTags.Any(tag => tag.Equals("reparse-point", StringComparison.OrdinalIgnoreCase)
                || tag.Equals("reparse-cycle", StringComparison.OrdinalIgnoreCase)
                || tag.Equals("duplicate-target", StringComparison.OrdinalIgnoreCase)))
            return (null, false);
        var path = Normalize(candidate.Path);
        if (path == "<invalid-path>" || !MatchesCandidateStructure(candidate, path)
            || IsLookupExcluded(path, exclusions)) return (null, false);

        // The full stable path identity is checked for every eligible scan entry.
        // Missing paths never touch the filesystem or software metadata.
        var shared = CreateSharedPathFingerprintNormalized(path, candidate.IsDirectory);
        var matched = shared is not null && index.ContainsFingerprint(shared) ? shared : null;
        var hasLegacyKeys = index.LegacyKeys is { Count: > 0 };
        if (matched is null && hasLegacyKeys)
        {
            var generic = CreatePurposeRoleFingerprint(candidate, path, null, "generic-v3");
            if (generic is not null && SharedFingerprintProtocol.CanReuse(generic)
                && index.ContainsFingerprint(generic)) matched = generic;
        }
        if (matched is not null)
            return SafeLookupAncestors(path) ? (matched, false) : (null, false);
        if (!hasLegacyKeys) return (null, true);

        // Old product identities remain valid. A snapshot can rule out unrelated
        // paths without Task.Run, registry scans, or ancestor attribute reads.
        if (identityResolver is ISoftwareIdentityLookupSnapshot softwareSnapshot
            && !softwareSnapshot.MayResolvePath(path)) return (null, true);
        if (identityResolver is not ISoftwareIdentityLookupSnapshot)
        {
            // Custom resolvers retain their original evidence contract.
            if (!SafeLookupAncestors(path)) return (null, false);
            var original = await ExtractAsync(candidate, exclusions, cancellationToken).ConfigureAwait(false);
            return original is not null && index.ContainsFingerprint(original)
                ? (original, false) : (null, true);
        }
        VerifiedSoftwareInstallation? installation = null;
        try { installation = await identityResolver.ResolveAsync(candidate with { Path = path }, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        if (installation is not { HasInstallationEvidence: true, HasProductEvidence: true, HasTrustedPublisherEvidence: true }
            || !Regex.IsMatch(installation.VersionMajor ?? "", "^[0-9]{1,4}$")
            || !IsSameOrDescendant(path, installation.InstallRoot)) return (null, true);
        var software = Regex.IsMatch(installation.SoftwareId, "^app-[a-f0-9]{64}$")
            ? installation.SoftwareId : "app-" + Digest("public-product-v2:" + installation.SoftwareId);
        var product = CreatePurposeRoleFingerprint(candidate, path, installation, software);
        if (product is null || !SharedFingerprintProtocol.CanReuse(product) || !index.ContainsFingerprint(product))
            return (null, true);
        return SafeLookupAncestors(path) ? (product, false) : (null, false);
    }

    /// <summary>Restore only an existing installation-local identity under the pre-catalog path rules.</summary>
    public Task<SharedFingerprint?> ExtractLegacyOwnObservationAsync(AiCandidateRecord candidate,
        IReadOnlyList<string> exclusions, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // This route cannot allocate a new identity, including when called on a contribution extractor.
        if (candidate.AnalysisEvidenceScope != AiAnalysisEvidenceScope.PathPurpose
            || !CanRestoreOwnObservationPath(candidate.Path)) return Task.FromResult<SharedFingerprint?>(null);
        return ExtractCoreAsync(candidate, exclusions, legacyOwnObservation: true, cancellationToken);
    }

    private async Task<SharedFingerprint?> ExtractCoreAsync(AiCandidateRecord candidate, IReadOnlyList<string> exclusions,
        bool legacyOwnObservation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var purposeOnly = candidate.AnalysisEvidenceScope == AiAnalysisEvidenceScope.PathPurpose;
        if (!ValidLocalPath(candidate.Path) || IsExcluded(candidate.Path, exclusions)
            || (purposeOnly ? candidate.Structure.Attributes.HasFlag(FileAttributes.ReparsePoint)
                || candidate.HasLimitedAnalysisEvidence : exclusions.Any(path => IsSameOrDescendant(path, candidate.Path)) || Unsafe(candidate.Structure))
            || !SafeExistingAncestors(candidate.Path)) return null;
        var path = Normalize(candidate.Path);
        VerifiedSoftwareInstallation? installation = null;
        try { installation = await identityResolver.ResolveAsync(candidate, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        var strong = installation is { HasInstallationEvidence: true, HasProductEvidence: true, HasTrustedPublisherEvidence: true }
            && Regex.IsMatch(installation.VersionMajor ?? "", "^[0-9]{1,4}$") && IsSameOrDescendant(path, installation.InstallRoot);
        var software = strong ? installation!.SoftwareId : "generic-v2";
        // Legacy identity plug-ins are evidence, not a list of supported applications.
        if (strong && !Regex.IsMatch(software, "^app-[a-f0-9]{64}$")) software = "app-" + Digest("public-product-v2:" + software);
        if (purposeOnly)
        {
            return PurposeFingerprint(candidate, path, strong ? installation : null, software, legacyOwnObservation);
        }
        var components = strong ? RelativeParts(path, Normalize(installation!.InstallRoot)) : GenericParts(path);
        if (!candidate.IsDirectory && components.Count > 0) components[^1] = "file-" + SharedFingerprintProtocol.ExtensionToken(Path.GetExtension(path));
        if (components.Count == 0) components.Add(candidate.IsDirectory ? "root" : "file-" + SharedFingerprintProtocol.ExtensionToken(Path.GetExtension(path)));
        if (components.Count > 12) return null;
        var tokens = components.Select((x, i) => SharedFingerprintProtocol.IsPathToken(x)
            || !candidate.IsDirectory && i == components.Count - 1 && x.StartsWith("file-", StringComparison.Ordinal) ? x : "mask");
        var shape = Shape(candidate, cancellationToken);
        if (shape is null) return null;
        var fingerprint = new SharedFingerprint(software, strong ? installation!.VersionMajor! : "0", "path2:" + string.Join('/', tokens), shape);
        return SharedFingerprintProtocol.IsValid(fingerprint) ? fingerprint : null;
    }

    private static SharedFingerprint? PurposeFingerprint(AiCandidateRecord candidate, string path,
        VerifiedSoftwareInstallation? installation, string software, bool legacy = false)
    {
        var fingerprint = CreatePurposeRoleFingerprint(candidate, path, installation, software, legacy);
        if (fingerprint is null) return null;
        return SharedFingerprintProtocol.CanReuse(fingerprint) ? fingerprint
            : CreateSharedPathFingerprintNormalized(path, candidate.IsDirectory);
    }

    private static SharedFingerprint? CreatePurposeRoleFingerprint(AiCandidateRecord candidate, string path,
        VerifiedSoftwareInstallation? installation, string software, bool legacy = false)
    {
        // Preserve existing public roles; all other complete paths get stable
        // cross-installation identities rather than installation-only observations.
        if (!MatchesCandidateStructure(candidate, path)) return null;
        List<string>? parts;
        if (installation is not null)
        {
            parts = RelativeParts(path, Normalize(installation.InstallRoot));
            if (parts.Count == 0) parts.Add("root");
        }
        else parts = KnownPurposeParts(path, legacy) ?? ["volume", .. RelativeParts(path, path[..3])];
        // Bounded anonymous observations retain an explicit unknown segment when depth is omitted.
        if (parts.Count > 12) parts = [.. parts.Take(10), "mask", parts[^1]];
        if (!candidate.IsDirectory)
        {
            if (parts.Count == 0) parts.Add("file-" + SharedFingerprintProtocol.ExtensionToken(Path.GetExtension(path)));
            else parts[^1] = "file-" + SharedFingerprintProtocol.ExtensionToken(Path.GetExtension(path));
        }
        var isProductRoot = installation is not null && path.Equals(Normalize(installation.InstallRoot), StringComparison.OrdinalIgnoreCase);
        var tokens = parts.Select((part, index) => (index == 0 && installation is null
                || part == "root" && parts.Count == 1 && isProductRoot && candidate.IsDirectory
                || SharedFingerprintProtocol.IsPurposePathToken(part)
                || !candidate.IsDirectory && index == parts.Count - 1 && part.StartsWith("file-", StringComparison.Ordinal)) ? part : "mask");
        var fingerprint = new SharedFingerprint(installation is null ? "generic-v3" : software,
            installation?.VersionMajor ?? "0", "path3:" + string.Join('/', tokens),
            candidate.IsDirectory ? "purpose3-directory" : "purpose3-file");
        return SharedFingerprintProtocol.IsValid(fingerprint) ? fingerprint : null;
    }

    private static List<string>? KnownPurposeParts(string path, bool legacy = false)
    {
        var anchor = (legacy ? LegacyPurposeRoots : PurposeRoots).Value.Find(path);
        return anchor is { } root ? [root.Role, .. RelativeParts(path, root.Path)] : null;
    }

    private sealed class PurposeRootIndex
    {
        private readonly PurposeRootNode _root = new();

        internal PurposeRootIndex(IReadOnlyList<(string Path, string Role)> roots)
        {
            foreach (var anchor in roots)
            {
                var node = _root;
                foreach (var part in anchor.Path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!node.Children.TryGetValue(part, out var child)) node.Children[part] = child = new();
                    node = child;
                }
                // Keep the same first role when special folders resolve to one path.
                node.Anchor ??= anchor;
            }
        }

        internal (string Path, string Role)? Find(string normalizedPath)
        {
            var node = _root;
            (string Path, string Role)? anchor = null;
            foreach (var part in normalizedPath.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!node.Children.TryGetValue(part, out var child)) break;
                node = child;
                if (node.Anchor is { } found) anchor = found;
            }
            return anchor;
        }

        private sealed class PurposeRootNode
        {
            internal readonly Dictionary<string, PurposeRootNode> Children = new(StringComparer.OrdinalIgnoreCase);
            internal (string Path, string Role)? Anchor;
        }
    }

    private static bool MatchesCandidateStructure(AiCandidateRecord candidate, string normalizedPath) =>
        candidate.IsDirectory == candidate.Structure.IsDirectory
        && (candidate.Path.Equals(candidate.Structure.Path, StringComparison.OrdinalIgnoreCase)
            || Normalize(candidate.Structure.Path).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<(string Path, string Role)> CreatePurposeRoots(bool legacy)
    {
        // Resolve actual OS locations; a folder named Users elsewhere is never an OS boundary.
        var roots = new List<(string Path, string Role)>();
        void Add(string root, string role)
        {
            if (ValidLocalPath(root) && Normalize(root).Length > 3) roots.Add((Normalize(root), role));
        }
        void AddSpecial(Environment.SpecialFolder folder, string role) => Add(Environment.GetFolderPath(folder), role);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (ValidLocalPath(profile))
        {
            Add(Path.GetDirectoryName(Normalize(profile)) ?? "", "os-users");
            Add(Path.Combine(profile, "AppData"), "os-appdata");
            Add(Path.Combine(profile, "AppData", "LocalLow"), "os-locallow");
            Add(Path.Combine(profile, "Downloads"), "os-downloads");
            if (!legacy)
                foreach (var directory in SharedStandardPurposeCatalog.ProfileDirectories)
                    Add(Path.Combine(profile, directory.Name), directory.Role);
        }
        if (!legacy)
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (ValidLocalPath(windows))
            {
                Add(windows, "os-windows");
                foreach (var directory in SharedStandardPurposeCatalog.WindowsDirectories)
                    Add(Path.Combine(windows, directory.Name), directory.Role);
            }
        }
        if (ValidLocalPath(local)) Add(Path.Combine(local, "Temp"), "os-temp");
        AddSpecial(Environment.SpecialFolder.UserProfile, "os-profile");
        AddSpecial(Environment.SpecialFolder.LocalApplicationData, "os-local");
        AddSpecial(Environment.SpecialFolder.ApplicationData, "os-roaming");
        AddSpecial(Environment.SpecialFolder.CommonApplicationData, "os-programdata");
        AddSpecial(Environment.SpecialFolder.ProgramFiles, "os-programfiles");
        AddSpecial(Environment.SpecialFolder.ProgramFilesX86, "os-programfiles-x86");
        AddSpecial(Environment.SpecialFolder.MyDocuments, "os-documents");
        AddSpecial(Environment.SpecialFolder.DesktopDirectory, "os-desktop");
        AddSpecial(Environment.SpecialFolder.MyPictures, "os-pictures");
        AddSpecial(Environment.SpecialFolder.MyVideos, "os-videos");
        AddSpecial(Environment.SpecialFolder.MyMusic, "os-music");
        return roots.OrderByDescending(root => root.Path.Length).ToArray();
    }

    private static List<string> RelativeParts(string path, string root) => path[root.Length..].TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries)
        .Select(x => x.ToLowerInvariant()).ToList();
    private static List<string> GenericParts(string path)
    {
        var parts = path[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries).Select(x => x.ToLowerInvariant()).ToList();
        // Discard account/drive only at an OS boundary. Arbitrary prefixes retain mask placeholders.
        if (parts.Count >= 3 && parts[0] == "users")
        {
            var tail = parts.Skip(2).ToList();
            var special = tail[0] switch
            {
                "downloads" => "user-downloads", "documents" => "user-documents", "desktop" => "user-desktop",
                "pictures" => "user-pictures", "videos" => "user-videos", "music" => "user-music", _ => ""
            };
            if (special.Length > 0) return [special, .. tail.Skip(1)];
            if (tail.Count >= 3 && tail[0] == "appdata" && tail[1] == "local" && tail[2] == "temp") return ["user-temp", .. tail.Skip(3)];
            if (tail.Count >= 2 && tail[0] == "appdata" && tail[1] is "local" or "roaming")
                return [tail[1] == "local" ? "user-local" : "user-roaming", .. tail.Skip(2)];
            return ["volume", "mask", .. tail];
        }
        return ["volume", .. parts];
    }

    private static string? Shape(AiCandidateRecord candidate, CancellationToken cancellationToken)
    {
        var stack = new Stack<(FileSystemNode Node, int Depth)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nodes = new List<FileSystemNode>();
        var complete = true;
        stack.Push((candidate.Structure, 0));
        while (stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (node, depth) = stack.Pop();
            if (node is null || node.Children is null || nodes.Count >= 20_000 || depth > 64 || Unsafe(node)
                || !ValidLocalPath(node.Path) || !seen.Add(Normalize(node.Path))
                || !node.Name.Equals(Path.GetFileName(node.Path), StringComparison.OrdinalIgnoreCase)
                || !node.IsDirectory && !node.Extension.Equals(Path.GetExtension(node.Path), StringComparison.OrdinalIgnoreCase)
                || !node.IsDirectory && node.Children.Count > 0) return null;
            nodes.Add(node);
            if (depth > 0 && node.IsDirectory && !SharedFingerprintProtocol.IsPathToken(node.Name.ToLowerInvariant())
                || !node.IsDirectory && SharedFingerprintProtocol.ExtensionToken(node.Extension) is "other" or "none") complete = false;
            foreach (var child in node.Children)
            {
                if (child is null || !Normalize(Path.GetDirectoryName(child.Path) ?? "").Equals(Normalize(node.Path), StringComparison.OrdinalIgnoreCase)) return null;
                stack.Push((child, depth + 1));
            }
        }
        var counts = new Dictionary<string, (int Files, int Directories)>(StringComparer.OrdinalIgnoreCase);
        var structureHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = nodes.Count - 1; i >= 0; i--)
        {
            var node = nodes[i];
            var files = node.IsDirectory ? node.Children.Sum(x => counts[Normalize(x.Path)].Files) : 1;
            var dirs = node.IsDirectory ? 1 + node.Children.Sum(x => counts[Normalize(x.Path)].Directories) : 0;
            if (files != node.DescendantFileCount || dirs != node.DescendantDirectoryCount) complete = false;
            counts[Normalize(node.Path)] = (files, dirs);
            var type = node.IsDirectory ? i == 0 ? "directory-root" : "d:" + node.Name.ToLowerInvariant()
                : "f:" + SharedFingerprintProtocol.ExtensionToken(node.Extension);
            if (node.IsDirectory && i > 0 && !SharedFingerprintProtocol.IsPathToken(node.Name.ToLowerInvariant())) type = "d:mask";
            var children = node.Children.Select(x => structureHashes[Normalize(x.Path)]).GroupBy(x => x)
                .OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key + "=" + CountBand(x.Count()));
            structureHashes[Normalize(node.Path)] = Digest(type + "[" + string.Join(';', children) + "]");
        }
        if (!Normalize(candidate.Path).Equals(Normalize(candidate.Structure.Path), StringComparison.OrdinalIgnoreCase)
            || candidate.IsDirectory != candidate.Structure.IsDirectory) return null;
        if (candidate.DescendantFileCount != candidate.Structure.DescendantFileCount) complete = false;
        // Only controlled types and coarse count bands; no private names, exact bytes or timestamps.
        return (complete ? "tree2-" : "partial2-") + structureHashes[Normalize(candidate.Path)];
    }

    private static int CountBand(int value) => value <= 4 ? value : (int)Math.Log2(value) + 4;
    private static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static bool Unsafe(FileSystemNode node) => node.Attributes.HasFlag(FileAttributes.ReparsePoint)
        || node.RiskTags.Any(x => x.Equals("scan-error", StringComparison.OrdinalIgnoreCase) || x.Equals("error", StringComparison.OrdinalIgnoreCase) || x.Equals("skipped", StringComparison.OrdinalIgnoreCase));
    private static bool ValidLocalPath(string path) => !string.IsNullOrWhiteSpace(path)
        && Regex.IsMatch(path, @"^[A-Za-z]:[\\/]") && !path[2..].Contains(':') && !path.Any(char.IsControl);

    private sealed class LookupPathNode
    {
        internal readonly Dictionary<string, LookupPathNode> Children = new(StringComparer.OrdinalIgnoreCase);
        internal bool Excluded;
    }

    private bool IsLookupExcluded(string normalizedPath, IReadOnlyList<string> exclusions)
    {
        if (!ReferenceEquals(_lookupExclusions, exclusions))
        {
            _lookupExclusionRoot = new LookupPathNode();
            foreach (var exclusion in exclusions)
            {
                var normalized = Normalize(exclusion);
                if (normalized == "<invalid-path>") continue;
                var entry = _lookupExclusionRoot;
                foreach (var part in normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!entry.Children.TryGetValue(part, out var child)) entry.Children[part] = child = new LookupPathNode();
                    entry = child;
                }
                entry.Excluded = true;
            }
            _lookupExclusions = exclusions;
        }
        var node = _lookupExclusionRoot!;
        foreach (var part in normalizedPath.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!node.Children.TryGetValue(part, out var child)) return false;
            node = child;
            if (node.Excluded) return true;
        }
        return false;
    }

    private bool SafeLookupAncestors(string normalizedPath)
    {
        var pending = new Stack<string>();
        var current = normalizedPath;
        while (!_lookupAncestorSafety.ContainsKey(current))
        {
            pending.Push(current);
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent)) break;
            current = parent;
        }
        if (_lookupAncestorSafety.TryGetValue(current, out var ancestorSafe) && !ancestorSafe) return false;
        while (pending.TryPop(out current))
        {
            var safe = true;
            try { safe = !File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { safe = false; }
            _lookupAncestorSafety[current] = safe;
            if (!safe) return false;
        }
        return true;
    }

    private static bool SafeExistingAncestors(string path)
    {
        try
        {
            var normalized = Normalize(path); var current = normalized[..3];
            foreach (var component in normalized[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, component);
                try { if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return false; }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }
    public static bool IsExcluded(string path, IReadOnlyList<string> exclusions) => exclusions.Any(item => IsSameOrDescendant(path, item));
    public static bool IsSameOrDescendant(string path, string parent)
    {
        var value = Normalize(path); var prefix = Normalize(parent);
        return value.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(prefix.EndsWith('\\') ? prefix : prefix + "\\", StringComparison.OrdinalIgnoreCase);
    }
    private static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "<invalid-path>";
        try { var full = Path.GetFullPath(path.Replace('/', '\\')); return full.Length > 3 ? full.TrimEnd('\\') : full; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return "<invalid-path>"; }
    }
}
