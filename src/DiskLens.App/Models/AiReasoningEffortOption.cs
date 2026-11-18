namespace DiskLens.App.Models;

public sealed record AiReasoningEffortOption(string Value, string Label)
{
    public static IReadOnlyList<AiReasoningEffortOption> All =>
    [
        new("", L.T("服务默认")),
        new("none", L.T("关闭")),
        new("low", L.T("低")),
        new("medium", L.T("中等")),
        new("high", L.T("高")),
        new("max", L.T("最高")),
    ];
}
