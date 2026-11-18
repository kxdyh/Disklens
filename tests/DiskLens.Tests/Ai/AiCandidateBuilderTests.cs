using DiskLens.Core.Ai;
using DiskLens.Core.Models;
using DiskLens.Core.Policies;

namespace DiskLens.Tests.Ai;

public sealed class AiCandidateBuilderTests
{
    [Fact]
    public void BuildCandidates_should_only_include_files_above_threshold_and_mark_protected_state()
    {
        var root = new FileSystemNode(
            Path: @"C:\",
            Name: "C:",
            Extension: string.Empty,
            SizeBytes: 900,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children:
            [
                new FileSystemNode(
                    Path: @"C:\Windows\Logs\big.etl",
                    Name: "big.etl",
                    Extension: ".etl",
                    SizeBytes: 700,
                    Attributes: FileAttributes.Normal,
                    CreatedAt: null,
                    ModifiedAt: null,
                    IsDirectory: false,
                    IsProtectedLocation: true,
                    RiskTags: ["system"],
                    Children: []),
                new FileSystemNode(
                    Path: @"C:\Users\tester\Downloads\small.tmp",
                    Name: "small.tmp",
                    Extension: ".tmp",
                    SizeBytes: 200,
                    Attributes: FileAttributes.Normal,
                    CreatedAt: null,
                    ModifiedAt: null,
                    IsDirectory: false,
                    IsProtectedLocation: false,
                    RiskTags: [],
                    Children: [])
            ]);

        var builder = new AiCandidateBuilder(new WindowsLocationPolicy());

        var candidates = builder.BuildCandidates(root, 512, 2400);

        var candidate = Assert.Single(candidates);
        Assert.Equal(@"C:\Windows\Logs\big.etl", candidate.Path);
        Assert.True(candidate.IsProtectedLocation);
    }

    [Fact]
    public void BuildCandidates_should_prioritize_larger_files_first()
    {
        var root = new FileSystemNode(
            Path: @"C:\",
            Name: "C:",
            Extension: string.Empty,
            SizeBytes: 3000,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children:
            [
                new FileSystemNode(
                    Path: @"C:\Users\tester\big.iso",
                    Name: "big.iso",
                    Extension: ".iso",
                    SizeBytes: 1800,
                    Attributes: FileAttributes.Normal,
                    CreatedAt: null,
                    ModifiedAt: null,
                    IsDirectory: false,
                    IsProtectedLocation: false,
                    RiskTags: [],
                    Children: []),
                new FileSystemNode(
                    Path: @"C:\Users\tester\medium.zip",
                    Name: "medium.zip",
                    Extension: ".zip",
                    SizeBytes: 900,
                    Attributes: FileAttributes.Normal,
                    CreatedAt: null,
                    ModifiedAt: null,
                    IsDirectory: false,
                    IsProtectedLocation: false,
                    RiskTags: [],
                    Children: []),
                new FileSystemNode(
                    Path: @"C:\Users\tester\small.tmp",
                    Name: "small.tmp",
                    Extension: ".tmp",
                    SizeBytes: 700,
                    Attributes: FileAttributes.Normal,
                    CreatedAt: null,
                    ModifiedAt: null,
                    IsDirectory: false,
                    IsProtectedLocation: false,
                    RiskTags: [],
                    Children: [])
            ]);

        var builder = new AiCandidateBuilder(new WindowsLocationPolicy());

        var candidates = builder.BuildCandidates(root, 512, 2400);

        Assert.Equal(3, candidates.Count);
        Assert.Equal(@"C:\Users\tester\big.iso", candidates[0].Path);
        Assert.Equal(@"C:\Users\tester\medium.zip", candidates[1].Path);
        Assert.Equal(@"C:\Users\tester\small.tmp", candidates[2].Path);
    }

    [Fact]
    public void BuildCandidates_should_prioritize_large_temp_directory_and_include_threshold_descendant_files()
    {
        var tempDirectory = new FileSystemNode(
            Path: @"C:\Users\tester\AppData\Local\Temp",
            Name: "Temp",
            Extension: string.Empty,
            SizeBytes: 1600,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children:
            [
                new FileSystemNode(
                    Path: @"C:\Users\tester\AppData\Local\Temp\cache\a.tmp",
                    Name: "a.tmp",
                    Extension: ".tmp",
                    SizeBytes: 1200,
                    Attributes: FileAttributes.Normal,
                    CreatedAt: null,
                    ModifiedAt: null,
                    IsDirectory: false,
                    IsProtectedLocation: false,
                    RiskTags: [],
                    Children: [])
            ]);

        var root = new FileSystemNode(
            Path: @"C:\",
            Name: "C:",
            Extension: string.Empty,
            SizeBytes: 2600,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children:
            [
                tempDirectory,
                new FileSystemNode(
                    Path: @"C:\Users\tester\Downloads\movie.iso",
                    Name: "movie.iso",
                    Extension: ".iso",
                    SizeBytes: 900,
                    Attributes: FileAttributes.Normal,
                    CreatedAt: null,
                    ModifiedAt: null,
                    IsDirectory: false,
                    IsProtectedLocation: false,
                    RiskTags: [],
                    Children: [])
            ]);

        var builder = new AiCandidateBuilder(new WindowsLocationPolicy());

        var candidates = builder.BuildCandidates(root, 512, 2400);

        Assert.Equal(3, candidates.Count);
        Assert.Equal(@"C:\Users\tester\AppData\Local\Temp", candidates[0].Path);
        Assert.True(candidates[0].IsDirectory);
        Assert.Equal(@"C:\Users\tester\AppData\Local\Temp\cache\a.tmp", candidates[1].Path);
        Assert.False(candidates[1].IsDirectory);
        Assert.Equal(@"C:\Users\tester\Downloads\movie.iso", candidates[2].Path);
        Assert.False(candidates[2].IsDirectory);
    }

    [Fact]
    public void BuildCandidates_should_include_ordinary_analysis_root_and_threshold_descendant()
    {
        var root = new FileSystemNode(
            Path: @"C:\Users\tester\AppData\Roaming\Example\Cache",
            Name: "Cache",
            Extension: string.Empty,
            SizeBytes: 1400,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children:
            [
                new FileSystemNode(
                    Path: @"C:\Users\tester\AppData\Roaming\Example\Cache\data.bin",
                    Name: "data.bin",
                    Extension: ".bin",
                    SizeBytes: 1400,
                    Attributes: FileAttributes.Normal,
                    CreatedAt: null,
                    ModifiedAt: null,
                    IsDirectory: false,
                    IsProtectedLocation: false,
                    RiskTags: [],
                    Children: [])
            ]);

        var builder = new AiCandidateBuilder(new WindowsLocationPolicy());

        var candidates = builder.BuildCandidates(root, 512, 2400);

        Assert.Equal(new[] { root.Path, root.Children[0].Path }, candidates.Select(candidate => candidate.Path));
        Assert.True(candidates[0].IsDirectory);
        Assert.False(candidates[1].IsDirectory);
    }

    [Fact]
    public void BuildCandidates_should_keep_large_directory_summary_when_real_payload_would_exceed_budget()
    {
        var cacheChildren = Enumerable.Range(1, 6)
            .Select(index => new FileSystemNode(
                Path: $@"C:\Users\tester\AppData\Local\Cache\segment-{index}",
                Name: $"segment-{index}",
                Extension: string.Empty,
                SizeBytes: 700,
                Attributes: FileAttributes.Directory,
                CreatedAt: null,
                ModifiedAt: null,
                IsDirectory: true,
                IsProtectedLocation: false,
                RiskTags: [],
                Children:
                [
                    new FileSystemNode(
                        Path: $@"C:\Users\tester\AppData\Local\Cache\segment-{index}\payload-{index}.bin",
                        Name: $"payload-{index}.bin",
                        Extension: ".bin",
                        SizeBytes: 700,
                        Attributes: FileAttributes.Normal,
                        CreatedAt: null,
                        ModifiedAt: null,
                        IsDirectory: false,
                        IsProtectedLocation: false,
                        RiskTags: [],
                        Children: [])
                ]))
            .ToArray();

        var root = new FileSystemNode(
            Path: @"C:\",
            Name: "C:",
            Extension: string.Empty,
            SizeBytes: 4200,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children:
            [
                new FileSystemNode(
                    Path: @"C:\Users\tester\AppData\Local\Cache",
                    Name: "Cache",
                    Extension: string.Empty,
                    SizeBytes: 4200,
                    Attributes: FileAttributes.Directory,
                    CreatedAt: null,
                    ModifiedAt: null,
                    IsDirectory: true,
                    IsProtectedLocation: false,
                    RiskTags: [],
                    Children: cacheChildren)
            ]);

        var builder = new AiCandidateBuilder(new WindowsLocationPolicy());

        var candidates = builder.BuildCandidates(root, 512, 300);

        var parentCandidate = Assert.Single(candidates, candidate =>
            string.Equals(candidate.Path, @"C:\Users\tester\AppData\Local\Cache", StringComparison.OrdinalIgnoreCase));
        Assert.True(parentCandidate.IsDirectory);
        Assert.NotEmpty(parentCandidate.Structure.Children);
        Assert.All(parentCandidate.Structure.Children, child => Assert.Empty(child.Children));

        Assert.Contains(candidates, candidate => string.Equals(candidate.Path, @"C:\Users\tester\AppData\Local\Cache\segment-1", StringComparison.OrdinalIgnoreCase));
        Assert.True(candidates.Count > 1);
    }

    [Fact]
    public void BuildCandidates_should_include_threshold_files_even_when_parent_directory_fits_budget()
    {
        var figmaDirectory = new FileSystemNode(
            Path: @"C:\Users\tester\.figma",
            Name: ".figma",
            Extension: string.Empty,
            SizeBytes: 2200,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children:
            [
                new FileSystemNode(
                    Path: @"C:\Users\tester\.figma\cache.bin",
                    Name: "cache.bin",
                    Extension: ".bin",
                    SizeBytes: 2200,
                    Attributes: FileAttributes.Normal,
                    CreatedAt: null,
                    ModifiedAt: null,
                    IsDirectory: false,
                    IsProtectedLocation: false,
                    RiskTags: [],
                    Children: [])
            ]);

        var gradleDirectory = new FileSystemNode(
            Path: @"C:\Users\tester\.gradle",
            Name: ".gradle",
            Extension: string.Empty,
            SizeBytes: 2100,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children:
            [
                new FileSystemNode(
                    Path: @"C:\Users\tester\.gradle\wrapper.zip",
                    Name: "wrapper.zip",
                    Extension: ".zip",
                    SizeBytes: 2100,
                    Attributes: FileAttributes.Normal,
                    CreatedAt: null,
                    ModifiedAt: null,
                    IsDirectory: false,
                    IsProtectedLocation: false,
                    RiskTags: [],
                    Children: [])
            ]);

        var usersDirectory = new FileSystemNode(
            Path: @"C:\Users",
            Name: "Users",
            Extension: string.Empty,
            SizeBytes: 6000,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children:
            [
                figmaDirectory,
                gradleDirectory,
                new FileSystemNode(
                    Path: @"C:\Users\tester\Documents",
                    Name: "Documents",
                    Extension: string.Empty,
                    SizeBytes: 700,
                    Attributes: FileAttributes.Directory,
                    CreatedAt: null,
                    ModifiedAt: null,
                    IsDirectory: true,
                    IsProtectedLocation: false,
                    RiskTags: [],
                    Children: [])
            ]);

        var root = new FileSystemNode(
            Path: @"C:\",
            Name: "C:",
            Extension: string.Empty,
            SizeBytes: 6000,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children:
            [
                usersDirectory
            ]);

        var builder = new AiCandidateBuilder(new WindowsLocationPolicy());

        var candidates = builder.BuildCandidates(root, 1024, 2400);

        Assert.Contains(candidates, candidate => string.Equals(candidate.Path, @"C:\Users", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(candidates, candidate => string.Equals(candidate.Path, @"C:\Users\tester\.figma", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(candidates, candidate => string.Equals(candidate.Path, @"C:\Users\tester\.gradle", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(candidates, candidate => string.Equals(candidate.Path, @"C:\Users\tester\.figma\cache.bin", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(candidates, candidate => string.Equals(candidate.Path, @"C:\Users\tester\.gradle\wrapper.zip", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildCandidates_should_include_descendant_changes_in_directory_signature()
    {
        var firstRoot = CreateRootWithNestedCache(modifiedAtUtcTicks: 638000000000000000);
        var secondRoot = CreateRootWithNestedCache(modifiedAtUtcTicks: 638000000000000001);
        var builder = new AiCandidateBuilder(new WindowsLocationPolicy());

        var firstSignature = builder
            .BuildCandidates(firstRoot, 512, 2400)
            .Single(candidate => string.Equals(candidate.Path, @"C:\Users\tester\AppData\Local\Cache", StringComparison.OrdinalIgnoreCase))
            .Signature;
        var secondSignature = builder
            .BuildCandidates(secondRoot, 512, 2400)
            .Single(candidate => string.Equals(candidate.Path, @"C:\Users\tester\AppData\Local\Cache", StringComparison.OrdinalIgnoreCase))
            .Signature;

        Assert.NotEqual(firstSignature, secondSignature);
    }

    private static FileSystemNode CreateRootWithNestedCache(long modifiedAtUtcTicks)
    {
        var child = new FileSystemNode(
            Path: @"C:\Users\tester\AppData\Local\Cache\data.bin",
            Name: "data.bin",
            Extension: ".bin",
            SizeBytes: 900,
            Attributes: FileAttributes.Normal,
            CreatedAt: null,
            ModifiedAt: new DateTimeOffset(new DateTime(modifiedAtUtcTicks, DateTimeKind.Utc)),
            IsDirectory: false,
            IsProtectedLocation: false,
            RiskTags: [],
            Children: []);

        var cache = new FileSystemNode(
            Path: @"C:\Users\tester\AppData\Local\Cache",
            Name: "Cache",
            Extension: string.Empty,
            SizeBytes: 900,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children: [child],
            DescendantFileCount: 1,
            DescendantDirectoryCount: 1);

        return new FileSystemNode(
            Path: @"C:\",
            Name: "C:",
            Extension: string.Empty,
            SizeBytes: 900,
            Attributes: FileAttributes.Directory,
            CreatedAt: null,
            ModifiedAt: null,
            IsDirectory: true,
            IsProtectedLocation: false,
            RiskTags: [],
            Children: [cache],
            DescendantFileCount: 1,
            DescendantDirectoryCount: 2);
    }
}
