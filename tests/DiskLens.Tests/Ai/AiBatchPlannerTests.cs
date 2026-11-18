using DiskLens.Core.Ai;
using DiskLens.Core.Models;

namespace DiskLens.Tests.Ai;

public sealed class AiBatchPlannerTests
{
    [Fact]
    public void CreateBatches_should_split_records_without_exceeding_token_budget()
    {
        var planner = new AiBatchPlanner();
        var records = new[]
        {
            CreateRecord("alpha.iso", 120, "archive"),
            CreateRecord("beta.vhdx", 140, "virtual-disk"),
            CreateRecord("gamma.zip", 110, "archive"),
        };

        var batches = planner.CreateBatches(records, maxEstimatedTokens: 160, maxBatchSize: 2);

        Assert.True(batches.Count >= 2);
        Assert.All(batches, batch => Assert.True(batch.EstimatedTokens <= 160));
        Assert.All(batches, batch => Assert.True(batch.Records.Count <= 2));
    }

    [Theory]
    [InlineData(-1, 1, 45)]
    [InlineData(0, 1, 45)]
    [InlineData(1, 1, 45)]
    [InlineData(20, 15, 3)]
    [InlineData(40, 15, 3)]
    [InlineData(64, 15, 3)]
    public void CreateBatches_should_honor_requested_upper_bounds_and_fit_bilingual_replies(
        int requestedSize, int expectedFirstBatchSize, int expectedBatchCount)
    {
        var planner = new AiBatchPlanner();
        var records = Enumerable.Range(1, 45)
            .Select(index => CreateRecord($"payload-{index}.bin", 8, "fixed-batch"))
            .ToArray();

        var batches = planner.CreateBatches(records, maxEstimatedTokens: 100_000, maxBatchSize: requestedSize);

        Assert.Equal(expectedBatchCount, batches.Count);
        Assert.Equal(expectedFirstBatchSize, batches[0].Records.Count);
        Assert.All(batches, batch => Assert.InRange(batch.Records.Count, 1, expectedFirstBatchSize));
        Assert.Equal(records, batches.SelectMany(batch => batch.Records));
        Assert.All(batches, batch => Assert.InRange(
            AiOutputTokenAllowance.ForRecordCount(batch.Records.Count), 1, 8192));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateBatches_should_still_split_by_token_budget_when_forty_items_are_allowed(bool useTokenSavingMode)
    {
        var records = Enumerable.Range(1, 45)
            .Select(index => CreateRecord($"payload-{index:D2}.bin", 8, "token-budget"))
            .ToArray();
        var tokenBudget = records.Max(record => AiPromptFactory.EstimateRecordTokens(record, useTokenSavingMode)) * 2;

        var batches = new AiBatchPlanner().CreateBatches(records, tokenBudget, 40, useTokenSavingMode);

        Assert.True(batches.Count > 2);
        Assert.All(batches, batch => Assert.InRange(batch.EstimatedTokens, 1, tokenBudget));
        Assert.All(batches, batch => Assert.InRange(batch.Records.Count, 1, 2));
        Assert.Equal(records, batches.SelectMany(batch => batch.Records));
    }

    private static AiCandidateRecord CreateRecord(string name, long sizeMb, string tag)
    {
        return new AiCandidateRecord(
            Path: $@"C:\Users\tester\Downloads\{name}",
            Name: name,
            SizeBytes: sizeMb * 1024 * 1024,
            IsDirectory: false,
            DescendantFileCount: 1,
            IsProtectedLocation: false,
            Signature: $"{name}:{tag}",
            Structure: new FileSystemNode(
                Path: $@"C:\Users\tester\Downloads\{name}",
                Name: name,
                Extension: Path.GetExtension(name),
                SizeBytes: sizeMb * 1024 * 1024,
                Attributes: FileAttributes.Normal,
                CreatedAt: null,
                ModifiedAt: null,
                IsDirectory: false,
                IsProtectedLocation: false,
                RiskTags: [],
                Children: []));
    }
}
