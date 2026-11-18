using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.App.Tests;

public sealed partial class SharedContributionOutboxBehaviorTests
{
    [Theory]
    [InlineData("Low")]
    [InlineData("Unknown")]
    [InlineData("")]
    public async Task New_low_or_unknown_observations_are_skipped_without_affecting_eligible_items(string grade)
    {
        using var outbox = Create();
        var rejected = Queued("rejected-grade-analysis", 'a');
        rejected = rejected with { Contribution = rejected.Contribution with { ConfidenceLevel = grade } };
        await outbox.EnqueueAsync(_consent.Value, [rejected, Queued("eligible-grade-analysis", 'b')]);

        Assert.Equal(1, await outbox.PendingCountAsync());
        var receipt = await outbox.FlushAsync();

        Assert.Equal(1, receipt.Accepted);
        Assert.Equal("eligible-grade-analysis", Assert.Single(Assert.Single(_client.Batches)).AnalysisId);
    }

    [Theory]
    [InlineData("Low")]
    [InlineData("Unknown")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Legacy_encrypted_queue_prunes_ineligible_records_durably_before_retry_without_losing_good_work(string? grade)
    {
        using (var original = Create())
            await original.EnqueueAsync(_consent.Value,
                [Queued("legacy-ineligible-analysis", 'a'), Queued("legacy-eligible-analysis", 'b')]);
        await RewriteLegacyGradeAsync("legacy-ineligible-analysis", grade);
        _client.Response = (_, _, call) => call == 1
            ? throw new HttpRequestException("synthetic response loss")
            : new SharedContributionReceipt(1, 0, _consent.Value.Epoch);
        using (var restarted = Create())
        {
            var receipt = await restarted.FlushAsync();
            Assert.Equal(1, receipt.Pending);
            Assert.DoesNotContain("无法安全读取", restarted.LastError ?? "");
            var item = Assert.Single(Assert.Single(_client.Batches));
            Assert.Equal("legacy-eligible-analysis", item.AnalysisId);
        }
        using (var reader = Create()) Assert.Equal(1, await reader.PendingCountAsync());
        _clock.Advance(TimeSpan.FromDays(1));
        using var retry = Create();
        Assert.Equal(1, (await retry.RetryPendingAsync()).Accepted);
        Assert.All(_client.Batches, batch => Assert.Equal("legacy-eligible-analysis", Assert.Single(batch).AnalysisId));
        Assert.Equal(0, await retry.PendingCountAsync());
    }

    [Fact]
    public async Task All_low_legacy_queue_is_removed_without_sending_or_pausing_future_good_work()
    {
        _client.Response = (_, _, _) => throw new HttpRequestException("synthetic legacy upload failure");
        using (var original = Create())
        {
            await original.EnqueueAsync(_consent.Value, [Queued("legacy-low-only-analysis")]);
            await original.FlushAsync();
        }
        _client.Batches.Clear();
        _client.Response = null;
        await RewriteLegacyGradeAsync("legacy-low-only-analysis", "Low");
        using var restarted = Create();

        Assert.Equal(0, (await restarted.FlushAsync()).Pending);
        Assert.Empty(_client.Batches);
        Assert.Null(restarted.LastError);
        await restarted.EnqueueAsync(_consent.Value, [Queued("subsequent-good-analysis", 'b')]);
        Assert.Equal(1, (await restarted.FlushAsync()).Accepted);
    }

    private async Task RewriteLegacyGradeAsync(string analysisId, string? grade)
    {
        var file = Assert.Single(Directory.EnumerateFiles(_directory));
        var plain = ProtectedData.Unprotect(await File.ReadAllBytesAsync(file), null, DataProtectionScope.CurrentUser);
        try
        {
            var document = JsonNode.Parse(plain)!.AsObject();
            var item = document["Items"]!.AsArray().Select(node => node!.AsObject())
                .Single(node => node["Contribution"]!["AnalysisId"]!.GetValue<string>() == analysisId);
            var contribution = item["Contribution"]!.AsObject();
            if (grade is null) contribution.Remove("ConfidenceLevel");
            else contribution["ConfidenceLevel"] = grade;
            var updated = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(document);
            try { await File.WriteAllBytesAsync(file, ProtectedData.Protect(updated, null, DataProtectionScope.CurrentUser)); }
            finally { CryptographicOperations.ZeroMemory(updated); }
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
