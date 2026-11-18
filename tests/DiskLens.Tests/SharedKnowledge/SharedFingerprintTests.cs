using System.Text.Json;
using DiskLens.Core.Models;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.Tests.SharedKnowledge;

public sealed class SharedFingerprintTests
{
    [Theory]
    [InlineData(@"C:\我的应用\WeGame", @"C:\我的应用\WeGame\temp")]
    [InlineData(@"D:\Games\WeGame", @"D:\Games\WeGame\temp")]
    public async Task Verified_roots_normalize_without_transmitting_private_path(string root, string path)
    {
        var extractor = new SharedFingerprintExtractor(new Identity(new("wegame", "6", root, true, true, true)));
        var fingerprint = await extractor.ExtractAsync(Record(path, Node(path, Node(path + @"\user-private-name.tmp", size: 1))), []);
        Assert.NotNull(fingerprint);
        Assert.StartsWith("app-", fingerprint.SoftwareId);
        Assert.Equal("6", fingerprint.VersionMajor);
        Assert.Equal("path2:temp", fingerprint.RoleId);
        Assert.StartsWith("tree2-", fingerprint.Shape);
        Assert.True(SharedFingerprintProtocol.CanReuse(fingerprint));
        var payload = JsonSerializer.Serialize(fingerprint);
        Assert.DoesNotContain("Games", payload);
        Assert.DoesNotContain("user-private-name", payload);
        Assert.DoesNotContain("C:", payload);
        Assert.DoesNotContain("我的应用", payload);
    }

    [Theory]
    [InlineData(null, @"C:\我的项目\WeGame\temp")]
    [InlineData(@"C:\Apps\WeGame", @"C:\Apps\WeGame\用户资料\temp")]
    [InlineData(@"C:\Apps\WeGame", @"C:\Apps\WeGame2\temp")]
    public async Task Coincident_names_and_private_internal_roles_can_contribute_without_becoming_reusable(string? root, string path)
    {
        var extractor = new SharedFingerprintExtractor(new Identity(root is null ? null : new("wegame", "6", root, true, true, true)));
        var fingerprint = await extractor.ExtractAsync(Record(path, Node(path, Node(path + @"\cache.tmp", size: 1))), []);
        Assert.NotNull(fingerprint);
        Assert.Contains("mask", fingerprint.RoleId);
        Assert.False(SharedFingerprintProtocol.CanReuse(fingerprint));
    }

    [Theory]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task Incomplete_installation_identity_uses_generic_nonreusable_contribution(bool installation, bool product, bool publisher)
    {
        var path = @"C:\Apps\WeGame\temp";
        var extractor = new SharedFingerprintExtractor(new Identity(new("wegame", "6", @"C:\Apps\WeGame", installation, product, publisher)));
        var fingerprint = await extractor.ExtractAsync(Record(path, Node(path, Node(path + @"\a.tmp", size: 1))), []);
        Assert.NotNull(fingerprint);
        Assert.Equal("generic-v2", fingerprint.SoftwareId);
        Assert.False(SharedFingerprintProtocol.CanReuse(fingerprint));
    }

    [Fact]
    public async Task Excluded_descendant_cannot_leak_through_parent_summary()
    {
        var path = @"D:\Android\temp";
        var extractor = new SharedFingerprintExtractor(new Identity(new("android-sdk", "36", @"D:\Android", true, true, true)));
        var record = Record(path, Node(path, Node(path + @"\a.tmp", size: 1)));
        Assert.Null(await extractor.ExtractAsync(record, [path + @"\a.tmp"]));
        Assert.Null(await extractor.ExtractAsync(record, [@"D:\Android"]));
        Assert.NotNull(await extractor.ExtractAsync(record, [@"D:\Android-other"]));
    }

    [Theory]
    [InlineData("cache.tmp", FileAttributes.ReparsePoint, "")]
    [InlineData("cache.tmp", FileAttributes.Normal, "scan-error")]
    [InlineData("cache.tmp", FileAttributes.Normal, "skipped")]
    public async Task Links_and_unreadable_trees_abstain(string name, FileAttributes attributes, string tag)
    {
        var path = @"D:\Android\temp";
        var child = Node(path + "\\" + name, size: 1) with { Attributes = attributes, RiskTags = tag.Length > 0 ? [tag] : [] };
        var extractor = new SharedFingerprintExtractor(new Identity(new("android-sdk", "36", @"D:\Android", true, true)));
        Assert.Null(await extractor.ExtractAsync(Record(path, Node(path, child)), []));
    }

    [Fact]
    public async Task Complete_structure_keeps_major_version_and_partial_structure_cannot_be_reused()
    {
        var path = @"D:\Android\emulator";
        var tree = Node(path, Node(path + @"\emulator.exe", size: 1), Node(path + @"\qemu"), Node(path + @"\lib64"));
        var extractor = new SharedFingerprintExtractor(new Identity(new("android-sdk", "36", @"D:\Android", true, true, true)));
        var complete = await extractor.ExtractAsync(Record(path, tree), []);
        Assert.NotNull(complete);
        Assert.Equal("36", complete.VersionMajor);
        Assert.Equal("path2:emulator", complete.RoleId);
        Assert.True(SharedFingerprintProtocol.CanReuse(complete));
        var partial = await extractor.ExtractAsync(Record(path, tree with { Children = tree.Children.Take(2).ToArray() }), []);
        Assert.NotNull(partial);
        Assert.StartsWith("partial2-", partial.Shape);
        Assert.False(SharedFingerprintProtocol.CanReuse(partial));
    }

    [Fact]
    public async Task Truncated_candidate_summary_can_contribute_but_cannot_hide_omitted_content_for_reuse()
    {
        var path = @"D:\Android\temp";
        var tree = Node(path, Node(path + @"\a.tmp", 1), Node(path + @"\private.docx", 1));
        var extractor = new SharedFingerprintExtractor(new Identity(new("android-sdk", "36", @"D:\Android", true, true, true)));
        var summary = tree with { Children = tree.Children.Take(1).ToArray() };
        var fingerprint = await extractor.ExtractAsync(Record(path, summary), []);
        Assert.NotNull(fingerprint);
        Assert.StartsWith("partial2-", fingerprint.Shape);
        Assert.False(SharedFingerprintProtocol.CanReuse(fingerprint));
    }

    [Fact]
    public async Task Nested_personal_content_is_anonymous_but_mismatched_child_paths_abstain()
    {
        var path = @"D:\Android\emulator";
        var tree = Node(path, Node(path + @"\emulator.exe", 1), Node(path + @"\qemu"), Node(path + @"\lib64", Node(path + @"\lib64\private.docx", 1)));
        var extractor = new SharedFingerprintExtractor(new Identity(new("android-sdk", "36", @"D:\Android", true, true, true)));
        var fingerprint = await extractor.ExtractAsync(Record(path, tree), []);
        Assert.NotNull(fingerprint);
        Assert.DoesNotContain("private", JsonSerializer.Serialize(fingerprint));
        Assert.StartsWith("tree2-", fingerprint.Shape);
        var temp = @"D:\Android\temp";
        Assert.Null(await extractor.ExtractAsync(Record(temp, Node(temp, Node(@"D:\Elsewhere\cache.tmp", 1))), []));
    }

    [Fact]
    public async Task Canonical_exclusion_with_dotdot_still_excludes()
    {
        var path = @"D:\Android\temp";
        var extractor = new SharedFingerprintExtractor(new Identity(new("android-sdk", "36", @"D:\Android", true, true, true)));
        Assert.Null(await extractor.ExtractAsync(Record(path, Node(path, Node(path + @"\a.tmp", 1))), [@"D:\Android\other\..\temp"]));
    }

    internal static AiCandidateRecord Record(string path, FileSystemNode node) => new(path, Path.GetFileName(path), node.SizeBytes, node.IsDirectory, node.DescendantFileCount, false, "local-private-signature", node);
    internal static FileSystemNode Node(string path, params FileSystemNode[] children) => Node(path, children, 0);
    internal static FileSystemNode Node(string path, long size) => Node(path, [], size);
    private static FileSystemNode Node(string path, FileSystemNode[] children, long size) => new(path, Path.GetFileName(path), Path.GetExtension(path), size > 0 ? size : children.Sum(x => x.SizeBytes),
        size > 0 ? FileAttributes.Normal : FileAttributes.Directory, null, null, size == 0, false, [], children,
        size > 0 ? 1 : children.Sum(x => x.DescendantFileCount), size > 0 ? 0 : 1 + children.Sum(x => x.DescendantDirectoryCount));
    private sealed class Identity(VerifiedSoftwareInstallation? installation) : ISoftwareIdentityResolver
    {
        public Task<VerifiedSoftwareInstallation?> ResolveAsync(AiCandidateRecord candidate, CancellationToken cancellationToken = default) => Task.FromResult(installation);
    }
}
