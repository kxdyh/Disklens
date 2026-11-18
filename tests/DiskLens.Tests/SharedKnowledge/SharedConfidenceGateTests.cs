using DiskLens.Core.Models;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.Tests.SharedKnowledge;

public sealed partial class SharedPipelineTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Direct_queued_fresh_and_historical_contributions_all_filter_low_unknown_and_missing_grades(bool historical, bool queued)
    {
        var consent = new Consent { Value = Active() with { ShareHistory = true } };
        var client = new Client();
        var outbox = queued ? new RecordingOutbox() : null;
        var pipeline = new SharedKnowledgePipeline(consent, new Extractor(), client, outbox: outbox);
        foreach (var result in new[]
        {
            Fresh() with { ConfidenceLevel = AiConfidenceLevel.Low, Confidence = .99 },
            Fresh() with { ConfidenceLevel = AiConfidenceLevel.Unknown, Confidence = .99 },
            Fresh() with { ConfidenceLevel = AiConfidenceLevel.High, Confidence = .2 },
            Fresh() with { ConfidenceLevel = AiConfidenceLevel.High, Confidence = double.NaN },
        })
        {
            var receipt = await pipeline.ContributeAsync([Candidate], [result],
                Provenance() with { ConsentAtAnalysisStart = consent.Value }, historical);
            Assert.Equal(0, receipt.Accepted);
            Assert.Equal(1, receipt.Skipped);
        }
        Assert.Equal(0, client.Uploads);
        if (outbox is not null) Assert.Empty(outbox.Items);
    }

    [Fact]
    public async Task A_rejected_low_item_does_not_change_or_suppress_an_independent_eligible_result()
    {
        var (second, original, _) = ConflictingPair();
        var low = original with { Path = second.Path, OriginalAnalysisId = "low-analysis-private", ConfidenceLevel = AiConfidenceLevel.Low,
            SuggestedAction = SuggestedAction.Keep };
        var client = new Client();

        var receipt = await new SharedKnowledgePipeline(new Consent(), new Extractor(), client)
            .ContributeAsync([Candidate, second], [original, low], Provenance());

        Assert.Equal(1, receipt.Accepted);
        Assert.Equal(1, receipt.Skipped);
        Assert.Equal(original.OriginalAnalysisId, Assert.Single(client.LastContribution!).AnalysisId);
        Assert.Equal(AiConfidenceLevel.Low, low.ConfidenceLevel);
        Assert.Equal(SuggestedAction.Keep, low.SuggestedAction);
    }
}
