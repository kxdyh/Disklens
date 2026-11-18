using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using DiskLens.Core.Models;
using DiskLens.Core.SharedKnowledge;
using Microsoft.Win32;

namespace DiskLens.App.Services.SharedKnowledge;

/// <summary>Local-only installation and signed-product metadata. Does not execute binaries or read user documents.</summary>
public sealed class WindowsSoftwareIdentityResolver : ISoftwareIdentityResolver
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IReadOnlyList<Registration>? _registrations;
    private DateTimeOffset _refreshedAt;
    private const int MaximumMetadataLength = 256;
    private const int MaximumExecutables = 12;
    private readonly Dictionary<string, (long Length, DateTime Time, ProductEvidence? Evidence)> _productCache = new(StringComparer.OrdinalIgnoreCase);

    public Task<VerifiedSoftwareInstallation?> ResolveAsync(AiCandidateRecord candidate, CancellationToken cancellationToken = default)
        => Task.Run(() => ResolveCoreAsync(candidate, cancellationToken), cancellationToken);

    public async Task<ISoftwareIdentityResolver> CreateLookupSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return new LookupSnapshot(this, []);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RefreshRegistrationsIfNeeded();
            return new LookupSnapshot(this, _registrations!.ToArray());
        }
        finally { _gate.Release(); }
    }

    /// <summary>Unverified local registration roots used only to avoid unnecessary lookup work.</summary>
    public async Task<IReadOnlyList<string>> GetRegisteredRootsAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return [];
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RefreshRegistrationsIfNeeded();
            cancellationToken.ThrowIfCancellationRequested();
            return _registrations!.Select(registration => registration.Root)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally { _gate.Release(); }
    }

    // Call only while holding _gate; discovery and trusted resolution share one snapshot.
    private void RefreshRegistrationsIfNeeded()
    {
        if (_registrations is null || _refreshedAt < DateTimeOffset.UtcNow.AddMinutes(-1))
        {
            _registrations = ReadRegistrations();
            _refreshedAt = DateTimeOffset.UtcNow;
            _productCache.Clear();
        }
    }

    private async Task<VerifiedSoftwareInstallation?> ResolveCoreAsync(AiCandidateRecord candidate, CancellationToken cancellationToken,
        IReadOnlyList<Registration>? lookupRegistrations = null, string? lookupRoot = null)
    {
        if (!OperatingSystem.IsWindows()) return null;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (lookupRegistrations is null) RefreshRegistrationsIfNeeded();
            if (!SafePath(candidate.Path)) return null;
            var registrationSnapshot = lookupRegistrations ?? _registrations!;
            var root = lookupRoot ?? SelectUncontestedRoot(candidate.Path, registrationSnapshot.Select(x => x.Root).ToArray());
            if (root is null) return null;
            var registrations = registrationSnapshot.Where(x => string.Equals(x.Root, root, StringComparison.OrdinalIgnoreCase)).ToArray();
            // Duplicate 32/64-bit registrations are common; competing products sharing a root are not proof of ownership.
            if (registrations.Select(x => x.Identity).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1) return null;
            foreach (var registration in registrations.Take(1))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!SharedFingerprintExtractor.IsSameOrDescendant(candidate.Path, registration.Root)) continue;
                try
                {
                    if (!SafePath(registration.Root)) continue;
                    if (registration.Software == "android-sdk")
                    {
                        var emulator = Path.Combine(registration.Root, "emulator", "emulator.exe");
                        var adb = Path.Combine(registration.Root, "platform-tools", "adb.exe");
                        var metadata = ReadProperties(Path.Combine(registration.Root, "emulator", "source.properties"));
                        var tools = ReadProperties(Path.Combine(registration.Root, "platform-tools", "source.properties"));
                        if (metadata.GetValueOrDefault("Pkg.Path") != "emulator"
                            || metadata.GetValueOrDefault("Pkg.Desc") != "Android Emulator"
                            || !TryMajor(metadata.GetValueOrDefault("Pkg.Revision"), out var major)
                            || !TryMajor(tools.GetValueOrDefault("Pkg.Revision"), out _)
                            || !Directory.Exists(Path.Combine(registration.Root, "emulator", "qemu"))
                            || !SafePath(Path.Combine(registration.Root, "emulator", "qemu"))
                            || !Directory.Exists(Path.Combine(registration.Root, "emulator", "lib64"))
                            || !SafePath(Path.Combine(registration.Root, "emulator", "lib64"))
                            || !TrustedFile(emulator, "Google LLC") || !TrustedFile(adb, "Google LLC")) continue;
                        // SDK is a collection of independently versioned packages: use the verified emulator major for this role family.
                        return new("android-sdk", major, registration.Root, true, true, true);
                    }
                    if (registration.Software == "wegame")
                    {
                        var executable = Path.Combine(registration.Root, "wegame.exe");
                        var version = ReadProductEvidence(executable);
                        if (version is null || !IsKnownTencent(version.SignerName)
                            || !TryNormalizePublicMetadata(version.ProductName, out var product) || product != "wegame"
                            || !IsKnownTencent(version.CompanyName)
                            || !TryMajor(version.ProductVersion, out var major)
                            || !TryMajor(registration.Version, out var registeredMajor) || major != registeredMajor) continue;
                        return new("wegame", major, registration.Root, true, true, true);
                    }
                    // Only inspect a bounded set of executable metadata directly inside the registered root.
                    // File names, private paths and user documents never contribute to the public identity.
                    var executables = Directory.EnumerateFiles(registration.Root, "*.exe", SearchOption.TopDirectoryOnly)
                        .Take(MaximumExecutables + 1).ToArray();
                    if (executables.Length > MaximumExecutables) continue;
                    var identities = new HashSet<(string Key, string Major)>();
                    foreach (var executable in executables)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var evidence = ReadProductEvidence(executable);
                        if (evidence is not null && TryCreatePublicIdentity(registration.Name, registration.Publisher,
                            registration.Version, evidence.ProductName, evidence.CompanyName, evidence.ProductVersion,
                            evidence.SignerName, out var softwareKey, out var versionMajor))
                            identities.Add((softwareKey, versionMajor));
                    }
                    if (identities.Count == 1)
                    {
                        var identity = identities.Single();
                        return new(identity.Key, identity.Major, registration.Root, true, true, true);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or System.Security.Cryptography.CryptographicException)
                { /* Inconclusive metadata must fall back to personal analysis. */ }
            }
            return null;
        }
        finally { _gate.Release(); }
    }

    private static IReadOnlyList<Registration> ReadRegistrations()
    {
        var entries = new List<Registration>();
        foreach (var variable in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (TryRoot(value, out var root)) entries.Add(new("android-sdk", root, "Android SDK Tools", "Google LLC", "0"));
        }
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var basis = RegistryKey.OpenBaseKey(hive, view);
                using var sdk = basis.OpenSubKey(@"SOFTWARE\Android SDK Tools");
                if (TryRoot(sdk?.GetValue("Path") as string, out var sdkRoot)) entries.Add(new("android-sdk", sdkRoot, "Android SDK Tools", "Google LLC", "0"));
                using var uninstall = basis.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                var names = uninstall.GetSubKeyNames();
                // A truncated snapshot could hide a competing nested registration.
                if (names.Length > 5000) return [];
                foreach (var name in names)
                {
                    try
                    {
                        using var key = uninstall.OpenSubKey(name);
                        var displayName = BoundedRegistrationValue(key, "DisplayName");
                        var publisher = BoundedRegistrationValue(key, "Publisher");
                        if (!TryRoot(key?.GetValue("InstallLocation") as string, out var root)) continue;
                        var software = TryNormalizePublicMetadata(displayName, out var product) && product == "wegame"
                            && IsKnownTencent(publisher) ? "wegame" : "app";
                        entries.Add(new(software, root, displayName, publisher, BoundedRegistrationValue(key, "DisplayVersion")));
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }
        return entries.DistinctBy(x => x.Identity, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x.Root.Length).ThenBy(x => x.Root, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string BoundedRegistrationValue(RegistryKey? key, string name)
        => key?.GetValue(name) is string value && value.Length <= MaximumMetadataLength ? value : "";

    private static bool TryRoot(string? value, out string root)
    {
        root = "";
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || !Path.IsPathFullyQualified(value)) return false;
        try
        {
            var fullPath = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            // Reject network/device roots before any existence check could touch them.
            if (fullPath.Length <= 3 || !SafePath(fullPath) || !Directory.Exists(fullPath)) return false;
            root = fullPath;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return false; }
    }
    private static bool TryMajor(string? value, out string major)
    {
        major = "";
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumMetadataLength || value.Any(char.IsControl)) return false;
        var match = Regex.Match(value.Trim(), @"^([0-9]{1,4})(?:\.[0-9]{1,9}){0,3}(?:[-+][A-Za-z0-9.-]{1,64})?$", RegexOptions.CultureInvariant);
        if (match.Success) major = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
        return match.Success;
    }

    private static string? SelectUncontestedRoot(string candidate, IReadOnlyList<string> roots)
    {
        var matches = roots.Where(root => SharedFingerprintExtractor.IsSameOrDescendant(candidate, root))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderByDescending(root => root.Length).ToArray();
        if (matches.Length != 1) return null;
        var selected = matches[0];
        // A parent candidate may contain a separately installed product; do not attribute that subtree to the parent.
        if (roots.Any(root => !string.Equals(root, selected, StringComparison.OrdinalIgnoreCase)
            && SharedFingerprintExtractor.IsSameOrDescendant(root, candidate))) return null;
        return selected;
    }

    // Pure evidence comparison: callers must separately establish the cryptographic trust of signerName.
    private static bool TryCreatePublicIdentity(string? registrationName, string? registrationPublisher,
        string? registrationVersion, string? productName, string? companyName, string? productVersion,
        string? signerName, out string softwareKey, out string versionMajor)
    {
        softwareKey = versionMajor = "";
        if (!TryNormalizeCompany(registrationPublisher, out var publisher)
            || !TryNormalizeCompany(companyName, out var company) || publisher != company
            || !TryNormalizeCompany(signerName, out var signer) || signer != company
            || !TryNormalizePublicMetadata(productName, out var product)
            || !TryNormalizePublicMetadata(registrationName, out _)
            || !TryMajor(registrationVersion, out var registeredMajor)
            || !TryMajor(productVersion, out var productMajor) || registeredMajor != productMajor) return false;

        var displayName = registrationName!.Trim();
        // These describe an installation, not a different product. Other arbitrary name differences abstain.
        for (var i = 0; i < 3; i++)
            displayName = Regex.Replace(displayName, @"\s+\((?:x64|x86|arm64|64-bit|32-bit|user|system|machine|per-user|per-machine)\)\s*$",
                "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!TryNormalizePublicMetadata(displayName, out var registeredProduct)) return false;
        if (TryNormalizePublicMetadata(registrationVersion, out var normalizedVersion)
            && registeredProduct.EndsWith(" " + normalizedVersion, StringComparison.Ordinal))
            registeredProduct = registeredProduct[..^(normalizedVersion.Length + 1)];
        if (WithoutPublisherPrefix(registeredProduct, company) != WithoutPublisherPrefix(product, company)) return false;

        // This digest contains verified public company/product metadata only. No path or executable name is included.
        var bytes = Encoding.UTF8.GetBytes(company + "\0" + product);
        softwareKey = "app-" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        versionMajor = productMajor;
        return true;
    }

    private static string WithoutPublisherPrefix(string product, string publisher)
        => product.StartsWith(publisher + " ", StringComparison.Ordinal) ? product[(publisher.Length + 1)..] : product;

    private static bool TryNormalizeCompany(string? value, out string normalized)
    {
        if (!TryNormalizePublicMetadata(value, out normalized)) return false;
        var words = normalized.Split(' ').ToList();
        while (words.Count > 1 && words[^1] is "inc" or "incorporated" or "llc" or "ltd" or "limited" or "corp" or "corporation" or "co" or "company")
            words.RemoveAt(words.Count - 1);
        normalized = string.Join(' ', words);
        return normalized.Length > 0;
    }

    private static bool TryNormalizePublicMetadata(string? value, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumMetadataLength
            || value.Any(character => char.IsControl(character) || char.GetUnicodeCategory(character) is
                UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse)
            || value.IndexOfAny(['\\', '/', ':', '\0']) >= 0) return false;
        string text;
        try { text = value.Normalize(NormalizationForm.FormKC).ToLowerInvariant(); }
        catch (ArgumentException) { return false; }
        if (text.IndexOfAny(['\\', '/', ':', '\0']) >= 0) return false;
        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            if (char.IsLetterOrDigit(character) || character is '+' or '#') builder.Append(character);
            else if (builder.Length > 0 && builder[^1] != ' ') builder.Append(' ');
        }
        normalized = builder.ToString().Trim();
        return normalized.Length is > 0 and <= MaximumMetadataLength;
    }

    private static bool IsKnownTencent(string? value)
        => TryNormalizeCompany(value, out var company)
            && company is "tencent" or "tencent technology shenzhen";

    private static Dictionary<string, string> ReadProperties(string path)
    {
        if (!SafePath(path) || !File.Exists(path) || new FileInfo(path).Length > 16 * 1024) return [];
        return File.ReadAllLines(path).Where(line => line.StartsWith("Pkg.", StringComparison.Ordinal) && line.Contains('='))
            .Select(line => line.Split('=', 2)).GroupBy(parts => parts[0]).ToDictionary(group => group.Key, group => group.First()[1].Trim());
    }
    private static bool SafePath(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || path.Any(char.IsControl)
                || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            var fullPath = Path.GetFullPath(path);
            if (fullPath.Length < 3 || !char.IsAsciiLetter(fullPath[0]) || fullPath[1] != ':'
                || fullPath[2] != Path.DirectorySeparatorChar || fullPath[2..].Contains(':')) return false;
            var drive = new DriveInfo(Path.GetPathRoot(fullPath)!);
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram)) return false;
            // Inspect ancestors from the drive down so a link is rejected before accessing anything beneath it.
            var ancestors = new Stack<string>();
            var current = fullPath;
            while (!string.IsNullOrEmpty(current))
            {
                ancestors.Push(current);
                current = Path.GetDirectoryName(current);
            }
            foreach (var ancestor in ancestors)
                if (File.GetAttributes(ancestor).HasFlag(FileAttributes.ReparsePoint)) return false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Security.SecurityException)
        { return false; }
    }
    private bool TrustedFile(string path, string expectedPublisher)
    {
        var evidence = ReadProductEvidence(path);
        return evidence is not null && string.Equals(evidence.SignerName, expectedPublisher, StringComparison.OrdinalIgnoreCase);
    }
    private ProductEvidence? ReadProductEvidence(string path)
    {
        try
        {
            if (!SafePath(path) || !File.Exists(path)) return null;
            var info = new FileInfo(path);
            var length = info.Length;
            var lastWrite = info.LastWriteTimeUtc;
            if (_productCache.TryGetValue(path, out var cached) && cached.Length == length && cached.Time == lastWrite) return cached.Evidence;
            var signer = VerifyAuthenticode(path);
            ProductEvidence? evidence = null;
            if (signer is not null)
            {
                var metadata = FileVersionInfo.GetVersionInfo(path);
                evidence = new(metadata.ProductName, metadata.CompanyName, metadata.ProductVersion, signer);
            }
            info.Refresh();
            if (!info.Exists || info.Length != length || info.LastWriteTimeUtc != lastWrite || !SafePath(path)) return null;
            _productCache[path] = (length, lastWrite, evidence);
            return evidence;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or System.Security.SecurityException or CryptographicException)
        { return null; }
    }
    private static string? VerifyAuthenticode(string path)
    {
        var file = new WinTrustFileInfo { Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(), Path = path };
        var ptr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(file, ptr, false);
            var data = new WinTrustData { Size = (uint)Marshal.SizeOf<WinTrustData>(), UiChoice = 2, UnionChoice = 1, FileInfo = ptr,
                // Do not start network requests merely to identify a local installation.
                ProviderFlags = 0x1000 | 0x10 };
            var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
            if (WinVerifyTrust(new IntPtr(-1), ref action, ref data) != 0) return null;
            using var signedCertificate = X509Certificate.CreateFromSignedFile(path);
            using var certificate = new X509Certificate2(signedCertificate);
            var subject = certificate.GetNameInfo(X509NameType.SimpleName, false);
            return TryNormalizePublicMetadata(subject, out _) ? subject : null;
        }
        finally { Marshal.DestroyStructure<WinTrustFileInfo>(ptr); Marshal.FreeHGlobal(ptr); }
    }
    private sealed class LookupSnapshot : ISoftwareIdentityLookupSnapshot
    {
        private readonly WindowsSoftwareIdentityResolver _owner;
        private readonly LookupRootNode _roots = new();
        private readonly Dictionary<string, Task<VerifiedSoftwareInstallation?>> _verified = new(StringComparer.OrdinalIgnoreCase);

        internal LookupSnapshot(WindowsSoftwareIdentityResolver owner, IReadOnlyList<Registration> registrations)
        {
            _owner = owner;
            foreach (var group in registrations.GroupBy(value => value.Root, StringComparer.OrdinalIgnoreCase))
            {
                var node = _roots;
                var ancestors = new List<LookupRootNode> { node };
                foreach (var part in group.Key.Split('\\', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (!node.Children.TryGetValue(part, out var child)) node.Children[part] = child = new LookupRootNode();
                    node = child;
                    ancestors.Add(node);
                }
                node.Registrations = group.ToArray();
                foreach (var ancestor in ancestors) ancestor.RegisteredSubtreeCount++;
            }
        }

        public bool MayResolvePath(string normalizedLocalPath) => SelectRoot(normalizedLocalPath) is not null;

        public Task<VerifiedSoftwareInstallation?> ResolveAsync(AiCandidateRecord candidate, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var selected = SelectRoot(candidate.Path);
            var registrations = selected?.Registrations;
            if (registrations is not { Length: 1 })
                return Task.FromResult<VerifiedSoftwareInstallation?>(null);
            var root = registrations[0].Root;
            lock (_verified)
            {
                if (_verified.TryGetValue(root, out var cached)) return cached;
                // Verify installation metadata once, including a negative result.
                // The extractor separately validates every matched candidate's ancestry.
                var verification = _owner.ResolveCoreAsync(candidate with { Path = root }, cancellationToken, registrations, root);
                _verified[root] = verification;
                return verification;
            }
        }

        public Task<ISoftwareIdentityResolver> CreateLookupSnapshotAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<ISoftwareIdentityResolver>(this);
        }

        private LookupRootNode? SelectRoot(string path)
        {
            var node = _roots;
            LookupRootNode? selected = null;
            var ancestorRootCount = 0;
            var complete = true;
            foreach (var part in path.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!node.Children.TryGetValue(part, out var child)) { complete = false; break; }
                node = child;
                if (node.Registrations is null) continue;
                ancestorRootCount++;
                selected = node;
            }
            if (ancestorRootCount != 1 || selected?.Registrations?.Length != 1) return null;
            // Preserve ownership ambiguity: neither overlapping ancestor roots nor
            // another registered product inside this candidate proves ownership.
            if (complete && node.RegisteredSubtreeCount > (node.Registrations is null ? 0 : 1)) return null;
            return selected;
        }

        private sealed class LookupRootNode
        {
            internal readonly Dictionary<string, LookupRootNode> Children = new(StringComparer.OrdinalIgnoreCase);
            internal Registration[]? Registrations;
            internal int RegisteredSubtreeCount;
        }
    }

    private sealed record ProductEvidence(string? ProductName, string? CompanyName, string? ProductVersion, string SignerName);
    private sealed record Registration(string Software, string Root, string Name, string Publisher, string Version)
    {
        public string Identity => string.Join('\0', Root, Software, Name.Trim(), Publisher.Trim(), Version.Trim());
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo { public uint Size; [MarshalAs(UnmanagedType.LPWStr)] public string Path; public IntPtr File; public IntPtr KnownSubject; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size; public IntPtr Policy; public IntPtr Sip; public uint UiChoice; public uint Revocation;
        public uint UnionChoice; public IntPtr FileInfo; public uint StateAction; public IntPtr StateData;
        public IntPtr Url; public uint ProviderFlags; public uint UiContext; public IntPtr SignatureSettings;
    }
    [DllImport("wintrust.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);
}
