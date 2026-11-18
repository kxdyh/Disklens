using System.Text.Json;
using DiskLens.Core.Models;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.Tests.SharedKnowledge;

public sealed class OpaqueObservationFingerprintTests
{
    private const string FirstId = "0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("directory", "path3:os-local/mask")]
    [InlineData("file", "path3:os-downloads/file-zip")]
    public void Opaque_shapes_are_valid_observations_and_never_reusable(string kind, string role)
    {
        var fingerprint = new SharedFingerprint("generic-v3", "0", role, $"purpose3-observation-{kind}-{FirstId}");
        Assert.True(SharedFingerprintProtocol.IsValid(fingerprint));
        Assert.False(SharedFingerprintProtocol.CanReuse(fingerprint));
        Assert.False(SharedFingerprintProtocol.CanReuse(fingerprint with { RoleId = "path3:os-local/cache" }));
    }

    [Theory]
    [InlineData("purpose3-observation-directory-short", "path3:os-local/mask")]
    [InlineData("purpose3-observation-directory-0123456789ABCDEF0123456789ABCDEF", "path3:os-local/mask")]
    [InlineData("purpose3-observation-file-0123456789abcdef0123456789abcdef", "path3:os-local/mask")]
    [InlineData("purpose3-observation-directory-0123456789abcdef0123456789abcdef", "path3:os-local/file-zip")]
    [InlineData("purpose3-observation-directory-0123456789abcdef0123456789abcdef", "path3:os-local/private-name")]
    [InlineData("purpose3-observation-file-0123456789abcdef0123456789abcdef", "path3:os-local/file-private")]
    public void Observation_shapes_cannot_escape_role_type_or_controlled_tail_validation(string shape, string role)
        => Assert.False(SharedFingerprintProtocol.IsValid(new("generic-v3", "0", role, shape)));

    [Fact]
    public async Task Different_masked_paths_get_distinct_keys_but_the_same_normalized_path_stays_stable()
    {
        var identities = new Identities();
        var extractor = new SharedFingerprintExtractor(new NoProduct(), identities);
        var first = await extractor.ExtractAsync(Candidate(@"D:\DiskLensOpaqueFixture\first-private"), []);
        var second = await extractor.ExtractAsync(Candidate(@"D:\DiskLensOpaqueFixture\second-private"), []);
        var repeated = await extractor.ExtractAsync(Candidate(@"D:/DiskLensOpaqueFixture/unused/../first-private/"), []);
        Assert.NotNull(first); Assert.NotNull(second); Assert.NotNull(repeated);
        Assert.Equal(first.RoleId, second.RoleId);
        Assert.NotEqual(first.Key, second.Key);
        Assert.Equal(first.Key, repeated.Key);
        Assert.StartsWith("purpose3-observation-directory-", first.Shape);
        Assert.All(new[] { first, second, repeated }, item => Assert.False(SharedFingerprintProtocol.CanReuse(item)));
        Assert.Equal(@"D:\DiskLensOpaqueFixture\first-private", identities.Paths[0]);
        Assert.Equal(identities.Paths[0], identities.Paths[2]);
        var wire = JsonSerializer.Serialize(first);
        Assert.DoesNotContain("first-private", wire);
        Assert.DoesNotContain("DiskLensOpaqueFixture", wire);
    }

    [Fact]
    public async Task Reusable_public_roles_do_not_allocate_a_local_observation_identity()
    {
        var identities = new Identities();
        var fingerprint = await new SharedFingerprintExtractor(new NoProduct(), identities).ExtractAsync(
            Candidate(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)), []);
        Assert.NotNull(fingerprint);
        Assert.Equal("purpose3-directory", fingerprint.Shape);
        Assert.True(SharedFingerprintProtocol.CanReuse(fingerprint));
        Assert.Empty(identities.Paths);
    }

    [Fact]
    public async Task Optional_provider_preserves_legacy_shapes_for_existing_integrations()
    {
        var fingerprint = await new SharedFingerprintExtractor(new NoProduct()).ExtractAsync(
            Candidate(@"D:\DiskLensOpaqueFixture\private-name"), []);
        Assert.NotNull(fingerprint);
        Assert.Equal("purpose3-directory", fingerprint.Shape);
        Assert.False(SharedFingerprintProtocol.CanReuse(fingerprint));
    }

    [Fact]
    public async Task File_observation_keeps_its_controlled_extension_tail_and_file_shape()
    {
        var observation = await new SharedFingerprintExtractor(new NoProduct(), new Identities()).ExtractAsync(
            Candidate(@"D:\DiskLensOpaqueFixture\personal-archive.zip", directory: false), []);
        Assert.NotNull(observation);
        Assert.StartsWith("purpose3-observation-file-", observation.Shape);
        Assert.EndsWith("/file-zip", observation.RoleId);
        Assert.True(SharedFingerprintProtocol.IsValid(observation));
        Assert.False(SharedFingerprintProtocol.CanReuse(observation));
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcdef00")]
    [InlineData("0123456789abcdef0123456789abcdef\n")]
    public async Task Invalid_or_unavailable_local_identity_abstains_without_reusing_a_legacy_bucket(string id)
    {
        var fingerprint = await new SharedFingerprintExtractor(new NoProduct(), new Identities(id)).ExtractAsync(
            Candidate(@"D:\DiskLensOpaqueFixture\private-name"), []);
        Assert.Null(fingerprint);
    }

    private static AiCandidateRecord Candidate(string path, bool directory = true)
    {
        var name = Path.GetFileName(path.TrimEnd('\\', '/'));
        var node = new FileSystemNode(path, name, Path.GetExtension(path), 1,
            directory ? FileAttributes.Directory : FileAttributes.Normal, null, null, directory, false, [], []);
        return new(path, name, 1, directory, 0, false, "opaque-fixture-signature", node)
        { AnalysisEvidenceScope = AiAnalysisEvidenceScope.PathPurpose };
    }
    private sealed class NoProduct : ISoftwareIdentityResolver
    {
        public Task<VerifiedSoftwareInstallation?> ResolveAsync(AiCandidateRecord candidate, CancellationToken cancellationToken = default)
            => Task.FromResult<VerifiedSoftwareInstallation?>(null);
    }
    private sealed class Identities(string? fixedId = null) : ISharedObservationIdentityProvider
    {
        private readonly Dictionary<string, string> _ids = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Paths { get; } = [];
        public Task<string> GetOrCreateAsync(string normalizedLocalPath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Paths.Add(normalizedLocalPath);
            if (fixedId is not null) return Task.FromResult(fixedId);
            if (!_ids.TryGetValue(normalizedLocalPath, out var id)) _ids[normalizedLocalPath] = id = _ids.Count.ToString("x32");
            return Task.FromResult(id);
        }
    }
}
