using System.Reflection;
using DiskLens.Core.Models;
using DiskLens.Core.Scanning;

namespace DiskLens.Tests.Scanning;

public sealed class ScanSnapshotPerformanceTests
{
    [Fact]
    public void Updating_one_branch_should_reuse_completed_subtrees_and_leave_previous_snapshots_unchanged()
    {
        var root = MutableTreeNode.Directory("root");
        var completed = root.AddDirectory("completed");
        completed.AddFile("old.bin", allocated: 8, logical: 12);
        var active = root.AddDirectory("active");
        active.AddFile("first.bin", allocated: 4, logical: 6);
        var before = root.Snapshot();
        var completedBefore = Child(before, "completed");
        var activeBefore = Child(before, "active");

        active.AddFile("second.bin", allocated: 32, logical: 40);
        var after = root.Snapshot();

        Assert.NotSame(before, after);
        Assert.Same(completedBefore, Child(after, "completed"));
        Assert.NotSame(activeBefore, Child(after, "active"));
        Assert.Same(Child(activeBefore, "first.bin"), Child(Child(after, "active"), "first.bin"));
        Assert.Equal(12L, before.SizeBytes);
        Assert.Equal(18L, before.LogicalSizeBytes);
        Assert.Equal(2, before.DescendantFileCount);
        Assert.Single(activeBefore.Children);
        Assert.Equal(44L, after.SizeBytes);
        Assert.Equal(58L, after.LogicalSizeBytes);
        Assert.Equal(3, after.DescendantFileCount);
        Assert.Equal(3, after.DescendantDirectoryCount);
        Assert.Equal(new[] { "active", "completed" }, after.Children.Select(node => node.Name));
        Assert.Equal(new[] { "second.bin", "first.bin" }, Child(after, "active").Children.Select(node => node.Name));
        Assert.Same(after, root.Snapshot());
    }

    [Fact]
    public void Zero_byte_children_error_nodes_and_risk_tags_should_invalidate_all_ancestor_snapshots()
    {
        var root = MutableTreeNode.Directory("root");
        var nested = root.AddDirectory("nested");
        var initial = root.Snapshot();

        nested.AddFile("zero.bin", allocated: 0, logical: 0);
        var withZero = root.Snapshot();
        Assert.NotSame(initial, withZero);
        Assert.Empty(Child(initial, "nested").Children);
        Assert.Equal(1, withZero.DescendantFileCount);

        nested.AddError("unread.bin", isDirectory: false);
        nested.AddError("unread-directory", isDirectory: true);
        var withErrors = root.Snapshot();
        Assert.NotSame(withZero, withErrors);
        Assert.Single(Child(withZero, "nested").Children);
        Assert.Equal(2, withErrors.DescendantFileCount);
        Assert.Equal(3, withErrors.DescendantDirectoryCount);
        Assert.Contains("error", Child(Child(withErrors, "nested"), "unread.bin").RiskTags);

        nested.AddRiskTag("scan-error");
        nested.AddRiskTag("enumeration-error: denied");
        var withRisk = root.Snapshot();
        Assert.NotSame(withErrors, withRisk);
        Assert.Empty(Child(withErrors, "nested").RiskTags);
        Assert.Contains("scan-error", Child(withRisk, "nested").RiskTags);
        Assert.Contains("enumeration-error: denied", Child(withRisk, "nested").RiskTags);
        Assert.Equal(0L, withRisk.SizeBytes);
        Assert.Equal(0L, withRisk.LogicalSizeBytes);
        Assert.Equal(2, withRisk.DescendantFileCount);
        Assert.Equal(new[] { "unread-directory", "unread.bin", "zero.bin" }, Child(withRisk, "nested").Children.Select(node => node.Name));
    }

    [Fact]
    public void Logical_only_size_changes_should_invalidate_snapshots_and_preserve_stable_case_insensitive_sorting()
    {
        var root = MutableTreeNode.Directory("root");
        var first = root.AddFile("same.bin", allocated: 0, logical: 0);
        root.AddFile("SAME.BIN", allocated: 0, logical: 0);
        var before = root.Snapshot();

        first.AddSize(allocated: 0, logical: 123);
        var after = root.Snapshot();

        Assert.NotSame(before, after);
        Assert.Equal(0L, before.LogicalSizeBytes);
        Assert.Equal(123L, after.LogicalSizeBytes);
        Assert.Equal(123L, after.Children[0].LogicalSizeBytes);
        Assert.Equal(0L, after.SizeBytes);
        Assert.Equal(new[] { "same.bin", "SAME.BIN" }, after.Children.Select(node => node.Name));
        Assert.Same(before.Children[1], after.Children[1]);
    }

    [Fact]
    public void Snapshot_allocation_after_a_local_update_should_not_include_completed_files()
    {
        var root = MutableTreeNode.Directory("root");
        for (var directoryIndex = 0; directoryIndex < 32; directoryIndex++)
        {
            var completed = root.AddDirectory($"completed-{directoryIndex:00}");
            for (var fileIndex = 0; fileIndex < 1_000; fileIndex++)
            {
                completed.AddFile($"file-{fileIndex:0000}.bin", allocated: 1, logical: 1);
            }
        }

        var active = root.AddDirectory("active");
        var before = root.Snapshot();
        active.AddFile("new.bin", allocated: 1, logical: 1);

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var after = root.Snapshot();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        // A full traversal allocates megabytes for these 32,000 completed files.
        // This generous budget covers only the changed directory and its parent.
        Assert.True(allocated < 128 * 1_024, $"A local snapshot update allocated {allocated:N0} bytes.");
        Assert.Equal(32_001, after.DescendantFileCount);
        Assert.Equal(32_000, before.DescendantFileCount);
        foreach (var completedBefore in before.Children.Where(node => node.Name.StartsWith("completed-", StringComparison.Ordinal)))
        {
            Assert.Same(completedBefore, Child(after, completedBefore.Name));
        }
    }

    private static FileSystemNode Child(FileSystemNode parent, string name) =>
        Assert.Single(parent.Children, node => node.Name == name);

    // Exercise the private in-memory accumulator without creating thousands of files
    // or introducing a public API solely for performance tests.
    private sealed class MutableTreeNode
    {
        private static readonly Type NodeType = typeof(DirectoryScanner).GetNestedType("MutableNode", BindingFlags.NonPublic)!;
        private static readonly ConstructorInfo Constructor = NodeType.GetConstructors().Single();
        private static readonly MethodInfo AddChildMethod = NodeType.GetMethod("AddChild")!;
        private static readonly MethodInfo AddSizeMethod = NodeType.GetMethod("AddSize")!;
        private static readonly MethodInfo AddRiskTagMethod = NodeType.GetMethod("AddRiskTag")!;
        private static readonly MethodInfo ToImmutableMethod = NodeType.GetMethod("ToImmutable")!;
        private readonly object _node;
        private readonly string _path;

        private MutableTreeNode(MutableTreeNode? parent, string name, bool isDirectory, long allocated = 0, long logical = 0, List<string>? riskTags = null)
        {
            _path = parent is null ? name : Path.Combine(parent._path, name);
            _node = Constructor.Invoke([
                parent?._node, _path, name, isDirectory ? string.Empty : Path.GetExtension(name), allocated,
                isDirectory ? FileAttributes.Directory : FileAttributes.Normal,
                null, null, isDirectory, false, riskTags ?? [], logical, null
            ]);
        }

        public static MutableTreeNode Directory(string name) => new(null, name, isDirectory: true);

        public MutableTreeNode AddDirectory(string name) => Add(new(this, name, isDirectory: true));

        public MutableTreeNode AddFile(string name, long allocated, long logical)
        {
            var child = Add(new(this, name, isDirectory: false, allocated, logical));
            AddSize(allocated, logical);
            return child;
        }

        public MutableTreeNode AddError(string name, bool isDirectory) =>
            Add(new(this, name, isDirectory, riskTags: ["error", "denied"]));

        public void AddSize(long allocated, long logical) => AddSizeMethod.Invoke(_node, [allocated, logical]);

        public void AddRiskTag(string tag) => AddRiskTagMethod.Invoke(_node, [tag]);

        public FileSystemNode Snapshot() => (FileSystemNode)ToImmutableMethod.Invoke(_node, null)!;

        private MutableTreeNode Add(MutableTreeNode child)
        {
            AddChildMethod.Invoke(_node, [child._node]);
            return child;
        }
    }
}
