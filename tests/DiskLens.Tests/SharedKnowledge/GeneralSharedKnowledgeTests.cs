using System.Text.Json;
using DiskLens.Core.Models;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.Tests.SharedKnowledge;

public sealed class GeneralSharedKnowledgeTests
{
    [Theory]
    [InlineData("file-private-name")]
    [InlineData("file-exe")]
    public async Task Raw_directory_names_cannot_impersonate_synthetic_file_type_tokens(string name)
    {
        var path = @"C:\Users\Alice\Downloads\" + name;
        var folder = SharedFingerprintTests.Node(path);
        var fingerprint = await new SharedFingerprintExtractor(new Identity(null))
            .ExtractAsync(SharedFingerprintTests.Record(path, folder), []);
        Assert.NotNull(fingerprint);
        Assert.Equal("path2:user-downloads/mask", fingerprint.RoleId);
        Assert.False(SharedFingerprintProtocol.CanReuse(fingerprint));
        Assert.DoesNotContain(name, JsonSerializer.Serialize(fingerprint));
    }

    [Fact]
    public async Task Legacy_anonymous_file_fingerprint_remains_readable_but_cannot_infer_purpose_from_extension()
    {
        var left = await ExtractFile(@"C:\Users\PrivateAlice\Downloads\personal-backup.zip", 123);
        var right = await ExtractFile(@"D:\Users\PrivateBob\Downloads\another-private-name.zip", 9876);
        Assert.NotNull(left);
        Assert.NotNull(right);
        Assert.Equal(left.Key, right.Key);
        Assert.Equal("generic-v2", left.SoftwareId);
        Assert.Equal("0", left.VersionMajor);
        Assert.Equal("path2:user-downloads/file-zip", left.RoleId);
        Assert.False(SharedFingerprintProtocol.CanReuse(left));
        var payload = JsonSerializer.Serialize(left);
        foreach (var privateValue in new[] { "PrivateAlice", "PrivateBob", "personal-backup", "another-private-name", "C:", "D:" })
            Assert.DoesNotContain(privateValue, payload);
        using var document = JsonDocument.Parse(payload);
        Assert.Equal(new[] { "SoftwareId", "VersionMajor", "RoleId", "Shape" },
            document.RootElement.EnumerateObject().Select(x => x.Name));
    }

    [Theory]
    [InlineData(@"C:\Users\Alice\Downloads\sample.zip", @"C:\Users\Alice\Documents\sample.zip")]
    [InlineData(@"D:\cache\sample.zip", @"E:\projects\cache\sample.zip")]
    [InlineData(@"C:\Users\Alice\AppData\Local\Temp\sample.zip", @"C:\Users\Alice\Downloads\sample.zip")]
    public async Task Identical_leaf_names_do_not_collapse_different_contexts(string leftPath, string rightPath)
    {
        var left = await ExtractFile(leftPath);
        var right = await ExtractFile(rightPath);
        Assert.NotNull(left);
        Assert.NotNull(right);
        Assert.NotEqual(left.Key, right.Key);
        Assert.NotEqual(left.RoleId, right.RoleId);
    }

    [Fact]
    public async Task Any_new_verified_product_can_contribute_without_a_catalog_entry()
    {
        const string firstRoot = @"D:\私有安装位置\UnlistedApp";
        const string secondRoot = @"E:\另一个安装位置\UnlistedApp";
        var first = await ExtractFile(firstRoot + @"\models\weights.safetensors", identity:
            new("unlisted-new-product", "42", firstRoot, true, true, true));
        var second = await ExtractFile(secondRoot + @"\models\renamed.safetensors", identity:
            new("unlisted-new-product", "42", secondRoot, true, true, true));
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.Key, second.Key);
        Assert.StartsWith("app-", first.SoftwareId);
        Assert.Equal("path2:models/file-safetensors", first.RoleId);
        Assert.True(SharedFingerprintProtocol.CanReuse(first));
        var serialized = JsonSerializer.Serialize(first);
        Assert.DoesNotContain("UnlistedApp", serialized);
        Assert.DoesNotContain("unlisted-new-product", serialized);
        Assert.DoesNotContain("私有安装位置", serialized);
        Assert.DoesNotContain("weights", serialized);
    }

    [Fact]
    public async Task Different_products_versions_and_internal_roles_never_share_a_bucket()
    {
        const string root = @"D:\Apps\ArbitraryProduct";
        var identity = new VerifiedSoftwareInstallation("brand-new-product", "1", root, true, true, true);
        var cache = await ExtractFile(root + @"\cache\entry.dat", identity: identity);
        var profile = await ExtractFile(root + @"\profiles\cache\entry.dat", identity: identity);
        var otherProduct = await ExtractFile(root + @"\cache\entry.dat", identity: identity with { SoftwareId = "different-product" });
        var otherVersion = await ExtractFile(root + @"\cache\entry.dat", identity: identity with { VersionMajor = "2" });
        Assert.NotNull(cache);
        Assert.NotNull(profile);
        Assert.NotNull(otherProduct);
        Assert.NotNull(otherVersion);
        Assert.Equal(4, new[] { cache.Key, profile.Key, otherProduct.Key, otherVersion.Key }.Distinct().Count());
    }

    [Fact]
    public async Task Extension_types_remain_distinct_without_sending_file_names()
    {
        var archive = await ExtractFile(@"C:\Users\Alice\Downloads\same-name.zip");
        var video = await ExtractFile(@"C:\Users\Alice\Downloads\same-name.mp4");
        Assert.NotNull(archive);
        Assert.NotNull(video);
        Assert.NotEqual(archive.Key, video.Key);
        Assert.NotEqual(archive.Shape, video.Shape);
        Assert.DoesNotContain("same-name", JsonSerializer.Serialize(archive));
        Assert.DoesNotContain("same-name", JsonSerializer.Serialize(video));
    }

    [Fact]
    public async Task Arbitrary_private_names_and_extensions_are_replaced_with_controlled_tokens()
    {
        var fingerprint = await ExtractFile(@"E:\PrivateCompany\SecretProject\personal-file.my-secret-extension");
        Assert.NotNull(fingerprint);
        Assert.Equal("path2:volume/mask/mask/file-other", fingerprint.RoleId);
        Assert.True(SharedFingerprintProtocol.IsValid(fingerprint));
        Assert.False(SharedFingerprintProtocol.CanReuse(fingerprint));
        var payload = JsonSerializer.Serialize(fingerprint);
        foreach (var privateValue in new[] { "PrivateCompany", "SecretProject", "personal-file", "secret-extension", "E:" })
            Assert.DoesNotContain(privateValue, payload);
    }

    [Fact]
    public async Task Ordinary_folder_and_personal_file_types_are_accepted_as_observations()
    {
        const string path = @"C:\Users\Alice\Downloads\cache";
        var tree = SharedFingerprintTests.Node(path, SharedFingerprintTests.Node(path + @"\private-document.docx", 1));
        var fingerprint = await new SharedFingerprintExtractor(new Identity(null)).ExtractAsync(SharedFingerprintTests.Record(path, tree), []);
        Assert.NotNull(fingerprint);
        Assert.Equal("path2:user-downloads/cache", fingerprint.RoleId);
        Assert.StartsWith("tree2-", fingerprint.Shape);
        Assert.DoesNotContain("private-document", JsonSerializer.Serialize(fingerprint));
    }

    [Theory]
    [InlineData(@"relative\private.zip")]
    [InlineData(@"\\server\private\sample.zip")]
    [InlineData(@"C:\Users\Alice\Downloads\sample.zip:secret")]
    public async Task Unsupported_or_ambiguous_local_paths_are_rejected(string path)
        => Assert.Null(await ExtractFile(path));

    [Fact]
    public async Task Drive_root_does_not_throw_or_become_an_automatically_reusable_folder()
    {
        const string path = @"C:\";
        var node = SharedFingerprintTests.Node(path);
        var fingerprint = await new SharedFingerprintExtractor(new Identity(null)).ExtractAsync(SharedFingerprintTests.Record(path, node), []);
        Assert.True(fingerprint is null || !SharedFingerprintProtocol.CanReuse(fingerprint));
    }

    [Fact]
    public async Task Inconsistent_file_extension_metadata_cannot_be_reused()
    {
        const string path = @"C:\Users\Alice\Downloads\sample.zip";
        var node = SharedFingerprintTests.Node(path, 1) with { Extension = ".tmp" };
        var fingerprint = await new SharedFingerprintExtractor(new Identity(null)).ExtractAsync(SharedFingerprintTests.Record(path, node), []);
        Assert.True(fingerprint is null || !SharedFingerprintProtocol.CanReuse(fingerprint));
    }

    [Theory]
    [InlineData(@"C:\Users\Alice\Downloads\personal.unknown-extension", "file-other")]
    [InlineData(@"C:\Users\Alice\Downloads\personal", "file-none")]
    public async Task Unidentified_file_type_can_contribute_but_cannot_be_automatically_reused(string path, string fileToken)
    {
        var fingerprint = await ExtractFile(path);
        Assert.NotNull(fingerprint);
        Assert.EndsWith(fileToken, fingerprint.RoleId);
        Assert.False(SharedFingerprintProtocol.CanReuse(fingerprint));
    }

    [Fact]
    public async Task Deletion_protection_does_not_hide_an_anonymized_file_observation()
    {
        const string path = @"C:\Users\Alice\Downloads\sample.zip";
        var node = SharedFingerprintTests.Node(path, 1) with { IsProtectedLocation = true };
        var fingerprint = await new SharedFingerprintExtractor(new Identity(null))
            .ExtractAsync(SharedFingerprintTests.Record(path, node), []);
        Assert.NotNull(fingerprint);
        Assert.False(SharedFingerprintProtocol.CanReuse(fingerprint));
        Assert.DoesNotContain("Alice", JsonSerializer.Serialize(fingerprint));
        Assert.DoesNotContain("sample", JsonSerializer.Serialize(fingerprint));
    }

    [Fact]
    public async Task Excluding_a_drive_root_excludes_every_descendant_without_excluding_other_drives()
    {
        const string path = @"C:\Users\Alice\Downloads\sample.zip";
        var extractor = new SharedFingerprintExtractor(new Identity(null));
        Assert.True(SharedFingerprintExtractor.IsSameOrDescendant(path, @"C:\"));
        Assert.False(SharedFingerprintExtractor.IsSameOrDescendant(path, @"D:\"));
        var record = SharedFingerprintTests.Record(path, SharedFingerprintTests.Node(path, 1));
        Assert.Null(await extractor.ExtractAsync(record, [@"C:\"]));
        Assert.NotNull(await extractor.ExtractAsync(record, [@"D:\"]));
    }

    [Fact]
    public async Task Moving_the_same_file_type_between_cache_and_userdata_changes_the_structure_fingerprint()
    {
        const string path = @"D:\Apps\GenericProduct\data";
        var identity = new VerifiedSoftwareInstallation("generic-product", "1", @"D:\Apps\GenericProduct", true, true, true);
        var cache = SharedFingerprintTests.Node(path,
            SharedFingerprintTests.Node(path + @"\cache", SharedFingerprintTests.Node(path + @"\cache\private-state.db", 1)),
            SharedFingerprintTests.Node(path + @"\userdata"));
        var userData = SharedFingerprintTests.Node(path,
            SharedFingerprintTests.Node(path + @"\cache"),
            SharedFingerprintTests.Node(path + @"\userdata", SharedFingerprintTests.Node(path + @"\userdata\private-state.db", 1)));
        var extractor = new SharedFingerprintExtractor(new Identity(identity));
        var first = await extractor.ExtractAsync(SharedFingerprintTests.Record(path, cache), []);
        var second = await extractor.ExtractAsync(SharedFingerprintTests.Record(path, userData), []);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first.Shape, second.Shape);
        Assert.NotEqual(first.Key, second.Key);
        Assert.DoesNotContain("private-state", JsonSerializer.Serialize(first));
        Assert.DoesNotContain("private-state", JsonSerializer.Serialize(second));
    }

    [Theory]
    [InlineData("private-directory")]
    [InlineData("private-extension")]
    public async Task Unknown_descendant_semantics_make_a_contribution_partial_and_ineligible_for_reuse(string kind)
    {
        const string path = @"C:\Users\Alice\Downloads\cache";
        var tree = kind == "private-directory"
            ? SharedFingerprintTests.Node(path, SharedFingerprintTests.Node(path + @"\private-project",
                SharedFingerprintTests.Node(path + @"\private-project\archive.zip", 1)))
            : SharedFingerprintTests.Node(path, SharedFingerprintTests.Node(path + @"\private-file.proprietary", 1));
        var fingerprint = await new SharedFingerprintExtractor(new Identity(null)).ExtractAsync(SharedFingerprintTests.Record(path, tree), []);
        Assert.NotNull(fingerprint);
        Assert.StartsWith("partial2-", fingerprint.Shape);
        Assert.False(SharedFingerprintProtocol.CanReuse(fingerprint));
        Assert.DoesNotContain("private", JsonSerializer.Serialize(fingerprint));
        Assert.DoesNotContain("proprietary", JsonSerializer.Serialize(fingerprint));
    }

    private static Task<SharedFingerprint?> ExtractFile(string path, long size = 1, VerifiedSoftwareInstallation? identity = null)
        => new SharedFingerprintExtractor(new Identity(identity)).ExtractAsync(
            SharedFingerprintTests.Record(path, SharedFingerprintTests.Node(path, size)), []);

    private sealed class Identity(VerifiedSoftwareInstallation? installation) : ISoftwareIdentityResolver
    {
        public Task<VerifiedSoftwareInstallation?> ResolveAsync(AiCandidateRecord candidate, CancellationToken cancellationToken = default)
            => Task.FromResult(installation);
    }
}
