using System.IO;
using DiskLens.Core.Ai;
using DiskLens.Core.Models;

namespace DiskLens.App.Models;

public sealed record AppSettings(
    string RootPath,
    long AnalysisThresholdMb,
    long VisualFolderDisplayThresholdMb,
    AiProviderKind ProviderKind,
    string AiEndpoint,
    string AiModel,
    int MaxAiBatchSize = 20,
    bool IsAiTokenSavingModeEnabled = false,
    string ThemeId = ThemeCatalog.DefaultThemeId,
    bool HasCompletedExplorerGuide = false,
    bool? IsReduceMotionEnabled = null,
    int MaxAiAnalysisTokens = 100_000,
    string AiReasoningEffort = "",
    string AiConnectionProfileId = "",
    bool HideAiDisclaimer = false,
    string Language = "zh")
{
    public static int NormalizeAiBatchSize(int value) =>
        Math.Clamp(value <= 0 ? 20 : value, 1, AiBatchPlanner.MaximumBatchSize);

    public static AppSettings Default()
    {
        var systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        return new AppSettings(
            RootPath: systemDrive + Path.DirectorySeparatorChar,
            AnalysisThresholdMb: 512,
            VisualFolderDisplayThresholdMb: 512,
            ProviderKind: AiProviderKind.Disabled,
            AiEndpoint: string.Empty,
            AiModel: string.Empty,
            MaxAiBatchSize: 20,
            IsAiTokenSavingModeEnabled: false,
            ThemeId: ThemeCatalog.DefaultThemeId,
            HasCompletedExplorerGuide: false,
            IsReduceMotionEnabled: null,
            MaxAiAnalysisTokens: 100_000);
    }
}
