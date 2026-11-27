using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.IO;
using DiskLens.App.Services.SharedKnowledge;

namespace DiskLens.App.Tests;

public sealed partial class SharedContributionOutboxBehaviorTests
{
    [Fact]
    public async Task Legacy_backoff_queue_receives_a_fresh_bounded_budget_and_waits_no_more_than_a_minute()
    {
        using (var old = Create())
            await old.EnqueueAsync(_consent.Value, [Queued("legacy-retry-analysis")]);
        var file = Assert.Single(Directory.EnumerateFiles(_directory));
        var plain = ProtectedData.Unprotect(await File.ReadAllBytesAsync(file), null, DataProtectionScope.CurrentUser);
        try
        {
            var json = JsonNode.Parse(plain)!.AsObject();
            json.Remove("RetryStates");
            json["FailureCount"] = 16;
            json["NextAttemptAt"] = _clock.GetUtcNow().AddHours(6);
            await File.WriteAllBytesAsync(file, ProtectedData.Protect(
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(json), null, DataProtectionScope.CurrentUser));
        }
        finally { CryptographicOperations.ZeroMemory(plain); }

        using var migrated = Create();
        var status = await migrated.GetUploadStatusAsync();
        Assert.Equal(0, status.AutomaticRetriesUsed);
        Assert.False(status.AutomaticRetriesExhausted);
        Assert.Equal(_clock.GetUtcNow().AddMinutes(1), status.NextRetryAt);
        await migrated.FlushAsync();
        Assert.Empty(_client.Batches);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await migrated.FlushAsync();
        Assert.Equal(0, await migrated.PendingCountAsync());
    }

    [Fact]
    public async Task Closing_between_batches_does_not_spend_the_budget_of_unsent_items()
    {
        using var outbox = Create();
        await outbox.EnqueueAsync(_consent.Value,
            Enumerable.Range(0, 12).Select(i => Queued($"unsent-retry-{i:D2}", "0123456789ab"[i])).ToArray());
        _client.Response = (_, _, _) => throw new HttpRequestException("temporary failure");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await outbox.FlushAsync();
            _clock.Advance(TimeSpan.FromMinutes(1));
        }
        using var close = new CancellationTokenSource();
        _client.Response = null;
        outbox.Changed += (_, _) =>
        {
            var snapshot = outbox.GetUploadStatusAsync().GetAwaiter().GetResult();
            if (snapshot.Pending == 2 && snapshot.Confirmed == 10) close.Cancel();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => outbox.FlushAsync(close.Token));

        using var restarted = Create();
        Assert.False((await restarted.GetUploadStatusAsync()).AutomaticRetriesExhausted);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await restarted.FlushAsync();
        Assert.Equal(0, await restarted.PendingCountAsync());
        Assert.Equal(2, _client.Batches[^1].Length);
    }

    [Fact]
    public async Task Retry_interval_survives_restart_and_network_changes_without_resetting_the_budget()
    {
        _client.Response = (_, _, _) => throw new HttpRequestException("temporary link failure");
        using (var first = Create())
        {
            await first.EnqueueAsync(_consent.Value, [Queued("retry-restart-analysis")]);
            await first.FlushAsync();
        }

        using var restarted = Create();
        _clock.Advance(TimeSpan.FromSeconds(59));
        var networkChange = typeof(SharedContributionOutbox).GetMethod("RetryAfterNetworkChangeAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)networkChange.Invoke(restarted, [CancellationToken.None])!;
        await restarted.FlushAsync();
        Assert.Single(_client.Batches);

        _clock.Advance(TimeSpan.FromSeconds(1));
        await restarted.FlushAsync();
        Assert.Equal(2, _client.Batches.Count);
        Assert.Equal(1, (await restarted.GetUploadStatusAsync()).AutomaticRetriesUsed);
        _clock.Advance(TimeSpan.FromMinutes(1));
        await restarted.FlushAsync();
        _clock.Advance(TimeSpan.FromMinutes(1));
        await restarted.FlushAsync();

        using var exhausted = Create();
        _clock.Advance(TimeSpan.FromDays(1));
        await (Task)networkChange.Invoke(exhausted, [CancellationToken.None])!;
        await exhausted.FlushAsync();
        Assert.Equal(4, _client.Batches.Count);
        Assert.True((await exhausted.GetUploadStatusAsync()).AutomaticRetriesExhausted);
        Assert.Equal(1, await exhausted.PendingCountAsync());
    }

    [Fact]
    public async Task Retry_stops_when_all_batches_are_acknowledged_and_does_not_repeat_successes()
    {
        using var outbox = Create();
        await outbox.EnqueueAsync(_consent.Value,
            Enumerable.Range(0, 12).Select(i => Queued($"partial-retry-{i:D2}", "0123456789ab"[i])).ToArray());
        _client.Response = (items, epoch, call) => call == 2
            ? throw new HttpRequestException("second batch failed") : new(items.Count, 0, epoch);
        await outbox.FlushAsync();
        Assert.Equal(2, await outbox.PendingCountAsync());
        _clock.Advance(TimeSpan.FromSeconds(59));
        await outbox.FlushAsync();
        Assert.Equal(2, _client.Batches.Count);
        _clock.Advance(TimeSpan.FromSeconds(1));
        await outbox.FlushAsync();

        Assert.Equal(3, _client.Batches.Count);
        Assert.Equal(_client.Batches[1], _client.Batches[2]);
        Assert.Equal(0, await outbox.PendingCountAsync());
        Assert.Equal(SharedUploadStage.Completed, (await outbox.GetUploadStatusAsync()).Stage);
        _clock.Advance(TimeSpan.FromHours(1));
        await outbox.FlushAsync();
        Assert.Equal(3, _client.Batches.Count);
    }

    [Fact]
    public async Task Exhausted_contributions_do_not_block_new_analysis_or_replenish_old_retries()
    {
        using var outbox = Create();
        _client.Response = (_, _, _) => throw new HttpRequestException("temporary service failure");
        var old = Queued("exhausted-old-analysis");
        await outbox.EnqueueAsync(_consent.Value, [old]);
        for (var call = 0; call < 4; call++)
        {
            await outbox.FlushAsync();
            _clock.Advance(TimeSpan.FromMinutes(1));
        }
        await outbox.EnqueueAsync(_consent.Value, [old]); // rediscovery cannot reset a spent budget
        await outbox.FlushAsync();
        Assert.Equal(4, _client.Batches.Count);

        await outbox.EnqueueAsync(_consent.Value, [Queued("fresh-new-analysis", 'b')]);
        _client.Response = null;
        await outbox.FlushAsync();
        Assert.Equal("fresh-new-analysis", Assert.Single(_client.Batches[^1]).AnalysisId);
        Assert.Equal(5, _client.Batches.Count);
        Assert.Equal(1, await outbox.PendingCountAsync());
        Assert.True((await outbox.GetUploadStatusAsync()).AutomaticRetriesExhausted);
    }
}
