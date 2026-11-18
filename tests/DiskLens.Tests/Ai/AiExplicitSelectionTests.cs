using System.Collections;
using DiskLens.Core.Ai;
using DiskLens.Core.Models;
using DiskLens.Core.Policies;

namespace DiskLens.Tests.Ai;

public sealed class AiExplicitSelectionTests
{
    [Fact]
    public void Empty_selection_returns_before_traversing_the_snapshot()
    {
        var root = Node(@"E:\Selected", true) with { Children = new UnreadableChildren() };
        Assert.Empty(new AiCandidateBuilder(new WindowsLocationPolicy()).BuildCandidates(
            root, 1024, 2400, includedPaths: new HashSet<string>()));
    }

    [Fact]
    public void Large_snapshot_constructs_only_selected_candidates_without_including_their_children()
    {
        var smallFiles = Enumerable.Range(0, 50_000)
            .Select(index => Node($@"E:\Selected\small-{index}.bin")).ToArray();
        var selectedChild = Node(@"E:\Selected\chosen\child.bin");
        var selected = Node(@"E:\Selected\chosen", true, [selectedChild]);
        var root = Node(@"E:\Selected", true, [.. smallFiles, selected]);
        var policy = new CountingPolicy();

        var candidates = new AiCandidateBuilder(policy).BuildCandidates(root, long.MaxValue, 2400,
            useTokenSavingMode: true, includedPaths: new HashSet<string> { selected.Path });

        var candidate = Assert.Single(candidates);
        Assert.Equal(selected.Path, candidate.Path);
        Assert.Same(selected, candidate.Structure);
        Assert.Equal(selected.Path, Assert.Single(policy.BuiltPaths));
        Assert.DoesNotContain(candidates, item => item.Path == selectedChild.Path);
    }

    [Fact]
    public void Explicit_selection_can_include_the_original_root_and_a_file_below_the_threshold()
    {
        var file = Node(@"E:\Selected\small.bin");
        var root = Node(@"E:\Selected", true, [file]);
        var candidates = new AiCandidateBuilder(new WindowsLocationPolicy()).BuildCandidates(root,
            long.MaxValue, 2400, true, new HashSet<string> { root.Path, file.Path });
        Assert.Equal(2, candidates.Count);
        Assert.Contains(candidates, item => item.Path == root.Path);
        Assert.Contains(candidates, item => item.Path == file.Path);
    }

    private static FileSystemNode Node(string path, bool directory = false,
        IReadOnlyList<FileSystemNode>? children = null) => new(path, Path.GetFileName(path),
            directory ? "" : ".bin", 1, directory ? FileAttributes.Directory : FileAttributes.Normal,
            null, null, directory, false, [], children ?? []);

    private sealed class CountingPolicy : ILocationPolicy
    {
        public List<string> BuiltPaths { get; } = [];
        public bool IsProtectedLocation(string path) { BuiltPaths.Add(path); return false; }
        public LocationAssessment AssessPath(string path, bool isDirectory, FileAttributes attributes) => throw new NotSupportedException();
        public IReadOnlyList<string> GetRiskTags(string path, bool isDirectory, FileAttributes attributes) => [];
    }

    private sealed class UnreadableChildren : IReadOnlyList<FileSystemNode>
    {
        public int Count => throw new InvalidOperationException("The snapshot must not be traversed.");
        public FileSystemNode this[int index] => throw new InvalidOperationException();
        public IEnumerator<FileSystemNode> GetEnumerator() => throw new InvalidOperationException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
