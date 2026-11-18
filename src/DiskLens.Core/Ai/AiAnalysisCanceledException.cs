using DiskLens.Core.Models;

namespace DiskLens.Core.Ai;

public sealed class AiAnalysisCanceledException : OperationCanceledException
{
    public AiAnalysisCanceledException(
        IReadOnlyList<AiAnalysisResult> partialResults,
        CancellationToken cancellationToken)
        : base("AI 分析已取消；已生成的候选结果仍可供界面显示。", cancellationToken)
    {
        PartialResults = partialResults.ToArray();
    }

    public IReadOnlyList<AiAnalysisResult> PartialResults { get; }

    public AiTokenBudgetReport? TokenBudget { get; internal set; }
}
