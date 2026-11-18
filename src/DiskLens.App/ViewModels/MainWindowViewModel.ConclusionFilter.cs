using System.IO;
using DiskLens.Core.Models;
using DiskLens.Core.Visualization;

namespace DiskLens.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private const string AllAnalysisConclusions = "AI分析结论";
    private string _analysisConclusionFilterMode = AllAnalysisConclusions;
    private FileSystemNode? _conclusionFilterRoot;
    private string? _cachedConclusionFilterMode;
    private bool _cachedConclusionSharedVisible;
    private IReadOnlyList<VisualExplorerItem> _conclusionFilterItems = [];
    private long _filteredOccupancyBytes;

    public IReadOnlyList<string> AnalysisConclusionFilterModes { get; } =
        [AllAnalysisConclusions, "可以考虑删", "不建议删"];

    public string AnalysisConclusionFilterMode
    {
        get => _analysisConclusionFilterMode;
        set
        {
            var normalized = value is "可以考虑删" or "不建议删" ? value : AllAnalysisConclusions;
            if (!SetProperty(ref _analysisConclusionFilterMode, normalized)) return;
            // A filtered view always follows size order, regardless of the previous sort choice.
            if (IsAnalysisConclusionFilterActive) DisplaySortMode = "大小";
            OnPropertyChanged(nameof(IsAnalysisConclusionFilterActive));
            OnPropertyChanged(nameof(IsDisplaySortEnabled));
            RebuildVisibleVisualExplorerItems();
        }
    }

    public bool IsAnalysisConclusionFilterActive => AnalysisConclusionFilterMode != AllAnalysisConclusions;

    public bool IsDisplaySortEnabled => !IsAnalysisConclusionFilterActive;

    public string TotalSizeLabelText => L.T(AnalysisConclusionFilterMode switch
    {
        "可以考虑删" => "当前可删占用",
        "不建议删" => "当前筛选占用",
        _ => "文件占用",
    });

    public string TotalSizeToolTip => L.T(IsAnalysisConclusionFilterActive
        ? "按当前 AI 结论和项目类型筛选合计，父目录与其子项不重复计入。AI 建议仅供参考，此值不等同于实际可释放空间。"
        : "当前目录的磁盘占用。");

    private void RefreshConclusionOccupancy()
    {
        // The projected list already includes conclusion and type filters. Never add a
        // matched child again when a matched directory already covers its disk usage.
        var nodes = IsAnalysisConclusionFilterActive
            ? _visibleVisualExplorerItems.Select(item => item.DisplayNode).ToArray()
            : [];
        var directories = nodes.Where(node => node.IsDirectory)
            .Select(node => node.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var node in nodes)
        {
            var path = node.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!seenPaths.Add(path)) continue;
            var covered = false;
            for (var parent = Path.GetDirectoryName(path); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
            {
                if (directories.Contains(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)))
                {
                    covered = true;
                    break;
                }
            }
            if (!covered) total += Math.Max(0, node.SizeBytes);
        }
        _filteredOccupancyBytes = total;
        OnPropertyChanged(nameof(TotalSizeText));
        OnPropertyChanged(nameof(TotalSizeLabelText));
        OnPropertyChanged(nameof(TotalSizeToolTip));
    }

    private IReadOnlyList<VisualExplorerItem> GetConclusionFilterItems(FileSystemNode? root)
    {
        if (root is null) return [];
        // Selecting a tile must not walk the entire disk again. A new immutable scan/AI
        // snapshot, directory, conclusion or sharing visibility invalidates this projection.
        if (!ReferenceEquals(_conclusionFilterRoot, root)
            || _cachedConclusionFilterMode != AnalysisConclusionFilterMode
            || _cachedConclusionSharedVisible != ShowSharedExperienceAnalysis)
        {
            _conclusionFilterRoot = root;
            _cachedConclusionFilterMode = AnalysisConclusionFilterMode;
            _cachedConclusionSharedVisible = ShowSharedExperienceAnalysis;
            _conclusionFilterItems = _visualExplorerBuilder.BuildFlatMatches(root, node =>
                VisibleAnalysisFor(node) is { } analysis && !analysis.LooksLikeFallback()
                && (AnalysisConclusionFilterMode == "可以考虑删"
                    ? analysis.SuggestedAction == SuggestedAction.RecycleCandidate
                    : analysis.SuggestedAction != SuggestedAction.RecycleCandidate));
        }
        return _conclusionFilterItems;
    }
}
