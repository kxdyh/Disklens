using System.Text.Json;
using DiskLens.Core.Models;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.Tests.SharedKnowledge;

public sealed class PathPurposeFingerprintTests
{
    private static readonly string Profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    [Fact]
    public async Task Known_OS_roles_do_not_need_complete_children_or_public_descendant_names()
    {
        var path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var plain = await Extract(path);
        var privateChild = SharedFingerprintTests.Node(Path.Combine(path, "private-user-content")) with
        { Attributes = FileAttributes.ReparsePoint, IsProtectedLocation = true, RiskTags = ["scan-error"] };
        var root = SharedFingerprintTests.Node(path, privateChild) with
        { DescendantFileCount = 900000, DescendantDirectoryCount = 800000, RiskTags = ["scan-error", "skipped"] };
        var partial = await Extract(path, root: root, incomplete: true);
        Assert.NotNull(plain);
        Assert.NotNull(partial);
        Assert.Equal("generic-v3", partial.SoftwareId);
        Assert.Equal("path3:os-local", partial.RoleId);
        Assert.Equal("purpose3-directory", partial.Shape);
        Assert.Equal(plain.Key, partial.Key);
        Assert.True(SharedFingerprintProtocol.CanReuse(partial));
        Assert.DoesNotContain("private-user-content", JsonSerializer.Serialize(partial));
        Assert.DoesNotContain(Profile, JsonSerializer.Serialize(partial));
    }

    [Fact]
    public async Task Environment_anchored_OS_roots_have_distinct_public_roles()
    {
        var cases = new (string Path, string Role)[] {
            (Path.GetDirectoryName(Profile)!, "os-users"), (Profile, "os-profile"),
            (Path.Combine(Profile, "AppData"), "os-appdata"),
            (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "os-local"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "os-roaming"),
            (Path.Combine(Profile, "AppData", "LocalLow"), "os-locallow"),
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp"), "os-temp"),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "os-programdata"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "os-programfiles"),
            (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "os-programfiles-x86"),
            (Path.Combine(Profile, "Downloads"), "os-downloads") };
        foreach (var item in cases.Where(x => !string.IsNullOrWhiteSpace(x.Path)).DistinctBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
        {
            var fingerprint = await Extract(item.Path);
            Assert.NotNull(fingerprint);
            Assert.Equal("path3:" + item.Role, fingerprint.RoleId);
        }
    }

    [Theory]
    [InlineData(@"D:\project\Users\private\AppData\Local\Temp")]
    [InlineData(@"D:\Users\private\Downloads")]
    [InlineData(@"D:\cache")]
    [InlineData(@"D:\private-prefix\cache")]
    public async Task Coincident_folder_names_elsewhere_are_only_anonymous_observations(string path)
    {
        var observation = await Extract(path);
        Assert.NotNull(observation);
        Assert.StartsWith("path3:volume/", observation.RoleId);
        Assert.False(SharedFingerprintProtocol.CanReuse(observation));
    }

    [Fact]
    public async Task Unknown_internal_names_and_another_accounts_name_are_not_dropped()
    {
        foreach (var path in new[] { Path.Combine(Profile, "private-project", "cache"),
            Path.Combine(Path.GetDirectoryName(Profile)!, "some-other-account", "AppData", "Local"), Path.Combine(Profile, "file-exe") })
        {
            var observation = await Extract(path);
            Assert.NotNull(observation);
            Assert.Contains("mask", observation.RoleId);
            Assert.False(SharedFingerprintProtocol.CanReuse(observation));
            Assert.DoesNotContain("private-project", JsonSerializer.Serialize(observation));
            Assert.DoesNotContain("some-other-account", JsonSerializer.Serialize(observation));
            Assert.DoesNotContain("file-exe", JsonSerializer.Serialize(observation));
        }
        var publicRole = await Extract(Path.Combine(Profile, "cache"));
        Assert.NotNull(publicRole);
        Assert.Equal("path3:os-profile/cache", publicRole.RoleId);
    }

    [Fact]
    public async Task Any_verified_product_can_share_its_public_root_and_controlled_internal_roles()
    {
        const string firstRoot = @"D:\PrivateInstall\NewProduct";
        const string secondRoot = @"E:\DifferentPrivateInstall\RenamedProduct";
        var identity = new VerifiedSoftwareInstallation("previously-unlisted-product", "42", firstRoot, true, true, true);
        var first = await Extract(firstRoot + @"\models\weights", identity);
        var second = await Extract(secondRoot + @"\models\weights", identity with { InstallRoot = secondRoot });
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.Key, second.Key);
        Assert.StartsWith("app-", first.SoftwareId);
        Assert.Equal("path3:models/weights", first.RoleId);
        Assert.DoesNotContain("previously-unlisted-product", JsonSerializer.Serialize(first));
        Assert.NotNull(await Extract(firstRoot, identity));
        var namedRoot = await Extract(firstRoot + @"\root", identity);
        Assert.NotNull(namedRoot);
        Assert.Equal("path3:mask", namedRoot.RoleId);
        Assert.False(SharedFingerprintProtocol.CanReuse(namedRoot));
        var privateRole = await Extract(firstRoot + @"\private-project\cache", identity);
        var weak = await Extract(firstRoot + @"\cache", identity with { HasTrustedPublisherEvidence = false });
        Assert.NotNull(privateRole); Assert.NotNull(weak);
        Assert.False(SharedFingerprintProtocol.CanReuse(privateRole));
        Assert.False(SharedFingerprintProtocol.CanReuse(weak));
        var other = await Extract(firstRoot + @"\models\weights", identity with { SoftwareId = "another-unlisted-product" });
        var version = await Extract(firstRoot + @"\models\weights", identity with { VersionMajor = "43" });
        Assert.NotEqual(first.Key, other!.Key);
        Assert.NotEqual(first.Key, version!.Key);
    }

    [Fact]
    public async Task Descendant_exclusions_do_not_affect_purpose_but_target_and_ancestor_exclusions_do()
    {
        var path = Path.Combine(Profile, "cache");
        Assert.NotNull(await Extract(path, exclusions: [Path.Combine(path, "private-child")]));
        Assert.Null(await Extract(path, exclusions: [path]));
        Assert.Null(await Extract(path, exclusions: [Profile]));
        Assert.NotNull(await Extract(path, root: SharedFingerprintTests.Node(path) with { IsProtectedLocation = true }));
        Assert.Null(await Extract(path, root: SharedFingerprintTests.Node(path) with { Attributes = FileAttributes.ReparsePoint }));
        var record = Record(path) with { IsProtectedLocation = true };
        Assert.NotNull(await new SharedFingerprintExtractor(new Identity(null)).ExtractAsync(record, []));
    }

    [Fact]
    public async Task Purpose_does_not_even_enumerate_a_large_unread_subtree_and_deep_observations_remain_masked()
    {
        var path = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = SharedFingerprintTests.Node(path) with { Children = new UnreadChildren(), DescendantFileCount = 1000000 };
        var fingerprint = await Extract(path, root: root, incomplete: true);
        Assert.NotNull(fingerprint);
        Assert.True(SharedFingerprintProtocol.CanReuse(fingerprint));
        var deepPath = @"D:\" + string.Join('\\', Enumerable.Repeat("cache", 40));
        var observation = await Extract(deepPath);
        Assert.NotNull(observation);
        Assert.Equal(12, observation.RoleId[6..].Split('/').Length);
        Assert.Contains("mask", observation.RoleId);
        Assert.False(SharedFingerprintProtocol.CanReuse(observation));
    }

    [Fact]
    public async Task Existing_junction_ancestor_is_rejected_even_when_candidate_metadata_is_clean()
    {
        if (!OperatingSystem.IsWindows()) return;
        var workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var fixture = Path.Combine(workspace, "artifacts", "purpose-fingerprint-tests", "junction-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(fixture, "target");
        var link = Path.Combine(fixture, "link");
        Directory.CreateDirectory(target);
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("powershell.exe")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("New-Item -ItemType Junction -Path '" + link.Replace("'", "''")
                + "' -Target '" + target.Replace("'", "''") + "' -ErrorAction Stop | Out-Null");
            using var process = System.Diagnostics.Process.Start(start)!;
            await process.WaitForExitAsync();
            Assert.Equal(0, process.ExitCode);
            Assert.True(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint));
            var path = Path.Combine(link, "cache");
            var identity = new VerifiedSoftwareInstallation("junction-product", "1", fixture, true, true, true);
            Assert.Null(await Extract(path, identity));
        }
        finally
        {
            // These generated, absolute targets remain inside this workspace's artifacts.
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(target);
            Directory.Delete(fixture);
        }
    }

    [Fact]
    public async Task Private_files_have_only_nonreusable_observations_even_in_downloads_or_a_product()
    {
        var path = Path.Combine(Profile, "Downloads", "personal-backup.zip");
        var observation = await Extract(path, root: SharedFingerprintTests.Node(path, 1));
        Assert.NotNull(observation);
        Assert.Equal("purpose3-file", observation.Shape);
        Assert.False(SharedFingerprintProtocol.CanReuse(observation));
        Assert.DoesNotContain("personal-backup", JsonSerializer.Serialize(observation));
        var product = new VerifiedSoftwareInstallation("new-product", "1", @"D:\Product", true, true, true);
        observation = await Extract(@"D:\Product\cache\one.zip", product, SharedFingerprintTests.Node(@"D:\Product\cache\one.zip", 1));
        Assert.NotNull(observation);
        Assert.False(SharedFingerprintProtocol.CanReuse(observation));
        var legacy = new SharedFingerprint("generic-v2", "0", "path2:user-downloads/file-zip", "tree2-" + new string('a', 64));
        Assert.True(SharedFingerprintProtocol.IsValid(legacy));
        Assert.False(SharedFingerprintProtocol.CanReuse(legacy));
        Assert.True(SharedFingerprintProtocol.CanReuse(legacy with { RoleId = "path2:user-downloads/cache" }));
    }

    [Fact]
    public void Protocol_keeps_purpose_and_structure_domains_separate_and_has_no_mask_escape_hatch()
    {
        var fingerprint = new SharedFingerprint("generic-v3", "0", "path3:os-local/cache", "purpose3-directory");
        Assert.True(SharedFingerprintProtocol.IsValid(fingerprint));
        foreach (var bad in new[] {
            fingerprint with { Shape = "tree2-" + new string('a', 64) }, fingerprint with { SoftwareId = "generic-v2" },
            fingerprint with { RoleId = "path2:user-local/cache" }, fingerprint with { RoleId = "path3:os-local/mask/cache" },
            fingerprint with { RoleId = "path3:os-local/file-zip" }, fingerprint with { RoleId = "path3:volume/cache" },
            fingerprint with { RoleId = "path3:os-local/os-temp" }, fingerprint with { RoleId = "path3:os-local/user-temp" } })
            Assert.False(SharedFingerprintProtocol.CanReuse(bad));
        foreach (var role in SharedFingerprintProtocol.PurposeRootTokens.Split(' '))
            Assert.True(SharedFingerprintProtocol.CanReuse(fingerprint with { RoleId = "path3:" + role }));
    }

    private static AiCandidateRecord Record(string path, FileSystemNode? root = null) => SharedFingerprintTests.Record(path,
        root ?? SharedFingerprintTests.Node(path)) with { AnalysisEvidenceScope = AiAnalysisEvidenceScope.PathPurpose };
    private static Task<SharedFingerprint?> Extract(string path, VerifiedSoftwareInstallation? identity = null,
        FileSystemNode? root = null, bool incomplete = false, IReadOnlyList<string>? exclusions = null)
        => new SharedFingerprintExtractor(new Identity(identity)).ExtractAsync(Record(path, root) with { HasIncompleteScan = incomplete }, exclusions ?? []);
    private sealed class Identity(VerifiedSoftwareInstallation? installation) : ISoftwareIdentityResolver
    {
        public Task<VerifiedSoftwareInstallation?> ResolveAsync(AiCandidateRecord candidate, CancellationToken cancellationToken = default)
            => Task.FromResult(installation);
    }
    private sealed class UnreadChildren : IReadOnlyList<FileSystemNode>
    {
        public int Count => 50000;
        public FileSystemNode this[int index] => throw new InvalidOperationException("A path-purpose fingerprint must not inspect children.");
        public IEnumerator<FileSystemNode> GetEnumerator() => throw new InvalidOperationException("A path-purpose fingerprint must not enumerate children.");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
