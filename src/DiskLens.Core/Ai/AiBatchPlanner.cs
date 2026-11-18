using DiskLens.Core.Models;

namespace DiskLens.Core.Ai;

public sealed class AiBatchPlanner
{
    public const int MaximumBatchSize = 40;

    public IReadOnlyList<AiBatch> CreateBatches(
        IReadOnlyList<AiCandidateRecord> records,
        int maxEstimatedTokens,
        int maxBatchSize,
        bool useTokenSavingMode = false)
    {
        if (records.Count == 0)
        {
            return [];
        }

        var safeTokenLimit = Math.Max(32, maxEstimatedTokens);
        // The setting is an upper bound. A bilingual reply also has to fit the
        // provider's output allowance, independently of the input token limit.
        var safeBatchSize = Math.Min(Math.Clamp(maxBatchSize, 1, MaximumBatchSize),
            AiOutputTokenAllowance.MaximumRecordsPerRequest);
        var batches = new List<AiBatch>();
        var currentRecords = new List<AiCandidateRecord>();
        var currentTokens = 0;

        foreach (var record in records)
        {
            var estimatedTokens = AiPromptFactory.EstimateRecordTokens(record, useTokenSavingMode);

            if (currentRecords.Count > 0
                && (currentTokens + estimatedTokens > safeTokenLimit || currentRecords.Count >= safeBatchSize))
            {
                batches.Add(new AiBatch(currentRecords.ToArray(), currentTokens, useTokenSavingMode));
                currentRecords.Clear();
                currentTokens = 0;
            }

            currentRecords.Add(record);
            currentTokens += estimatedTokens;
        }

        if (currentRecords.Count > 0)
        {
            batches.Add(new AiBatch(currentRecords.ToArray(), currentTokens, useTokenSavingMode));
        }

        return batches;
    }
}
