using DiskLens.Core.Models;

namespace DiskLens.Core.Visualization;

public sealed record VisualExplorerOptions(
    long FolderDisplayThresholdBytes = 0,
    string? ExpandedPath = null,
    int MaxDirectFilesPerDirectory = 50,
    int MaxDepth = 1);

public enum VisualExplorerItemKind
{
    Directory,
    File,
    Aggregate,
}

public enum VisualExplorerItemWeight
{
    Hero,
    Standard,
    Compact,
}

public enum VisualExplorerPalette
{
    Other,
    UserSpace,
    System,
    Applications,
    Shared,
    UserFolder,
    UserMoss,
    UserSlate,
    SystemFolder,
    ApplicationsFolder,
    SharedFolder,
    OtherFolder,
}

public sealed record VisualExplorerItem(
    FileSystemNode DisplayNode,
    VisualExplorerItemKind Kind,
    VisualExplorerItemWeight Weight,
    int Depth,
    string SizeText,
    string SubtitleText,
    string TooltipPathText,
    bool IsExpanded,
    IReadOnlyList<VisualExplorerItem> Children)
{
    public bool HasChildren => Children.Count > 0;

    public bool IsSelected { get; init; }

    public VisualExplorerPalette Palette { get; init; } = VisualExplorerPalette.Other;
}
