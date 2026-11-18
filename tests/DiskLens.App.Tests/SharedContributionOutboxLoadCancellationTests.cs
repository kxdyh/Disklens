using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using DiskLens.App.Services.SharedKnowledge;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.App.Tests;

public sealed class SharedContributionOutboxLoadCancellationTests
{
    private readonly string _directory;
    private readonly FixedClock _clock = new();
    private readonly ConsentSource _consent;
    private readonly RecordingClient _client = new();

    public SharedContributionOutboxLoadCancellationTests()
    {
        var artifacts = Environment.GetEnvironmentVariable("DISKLENS_TEST_ARTIFACTS")
            ?? Path.Combine(Path.GetTempPath(), "DiskLens.Outbox.Tests");
        _directory = Path.Combine(artifacts, "load-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _consent = new(new(true, SharedConsentSnapshot.CurrentAgreementVersion, false,
            _clock.GetUtcNow().AddHours(-1), 7, "synthetic-policy", [],
            new("synthetic-installation", "synthetic-test-token", 7)));
    }

    [Fact]
    public async Task Canceled_first_load_then_enqueue_keeps_persisted_analysis_ids_across_restart()
    {
        var original = Queued("original-before-canceled-load", 'a');
        using (var writer = Create()) await writer.EnqueueAsync(_consent.Value, [original]);
        var initialBytes = await File.ReadAllBytesAsync(QueuePath);
        using (var canceledReader = Create())
        {
            await CancelAtReadBoundaryAsync(canceledReader);
            Assert.Equal(initialBytes, await File.ReadAllBytesAsync(QueuePath));
            var added = Queued("new-after-canceled-load", 'b');
            await canceledReader.EnqueueAsync(_consent.Value, [added]);
            Assert.Equal(2, await canceledReader.PendingCountAsync());
        }
        using var restarted = Create();

        var receipt = await restarted.FlushAsync();

        Assert.Equal(2, receipt.Accepted);
        Assert.Equal(0, receipt.Pending);
        Assert.Equal(new[] { "new-after-canceled-load", "original-before-canceled-load" },
            Assert.Single(_client.Batches).Select(item => item.AnalysisId).Order(StringComparer.Ordinal));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task Canceled_first_load_can_retry_pending_count_and_flush_the_original_analysis()
    {
        var original = Queued("retry-original-analysis", 'a');
        using (var writer = Create()) await writer.EnqueueAsync(_consent.Value, [original]);
        using var reader = Create();
        await CancelAtReadBoundaryAsync(reader);

        Assert.Equal(1, await reader.PendingCountAsync());
        var receipt = await reader.FlushAsync();

        Assert.Equal(1, receipt.Accepted);
        Assert.Equal(original.Contribution, Assert.Single(Assert.Single(_client.Batches)));
        Assert.Equal(0, await reader.PendingCountAsync());
        Assert.Null(reader.LastError);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_existing_queue_stays_halted_and_cannot_be_overwritten_after_load(bool validEncryption)
    {
        var bytes = validEncryption
            ? ProtectedData.Protect(Encoding.UTF8.GetBytes("{\"Version\":999,\"Items\":[]}"), null, DataProtectionScope.CurrentUser)
            : new byte[] { 1, 2, 3, 4 };
        await File.WriteAllBytesAsync(QueuePath, bytes);
        using var reader = Create();

        Assert.Equal(0, await reader.PendingCountAsync());
        Assert.False(string.IsNullOrWhiteSpace(reader.LastError));
        await Assert.ThrowsAsync<IOException>(() => reader.EnqueueAsync(_consent.Value, [Queued("new-after-invalid-queue", 'b')]));
        await reader.FlushAsync();

        Assert.Equal(bytes, await File.ReadAllBytesAsync(QueuePath));
        Assert.Empty(_client.Batches);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    private static async Task CancelAtReadBoundaryAsync(SharedContributionOutbox outbox)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        // Public calls first wait for the state semaphore. Invoke the private read
        // boundary to deterministically cancel after that wait, before disk I/O.
        var load = typeof(SharedContributionOutbox).GetMethod("LoadLockedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var read = (Task)load.Invoke(outbox, [cancellation.Token])!;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
    }

    private string QueuePath => Path.Combine(_directory, "shared-contribution-outbox.bin");
    private SharedContributionOutbox Create() => new(_directory, _consent, _client, _clock);
    private SharedQueuedContribution Queued(string id, char shape) => new(
        new(id, "generic-v2", "0", "path2:user-temp", "tree2-" + new string(shape, 64), "cache", "Review",
            "fresh-ai", "official", "openai", true, false, "High"), _clock.GetUtcNow());

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class ConsentSource(SharedConsentSnapshot value) : ISharedConsentSource
    {
        public SharedConsentSnapshot Value { get; } = value;
        public Task<SharedConsentSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Value);
        }
    }

    private sealed class RecordingClient : ISharedExperienceClient
    {
        public List<SharedContribution[]> Batches { get; } = [];
        public Task<SharedContributionReceipt> ContributeAsync(SharedInstallation installation, long epoch,
            IReadOnlyList<SharedContribution> items, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Batches.Add(items.ToArray());
            return Task.FromResult(new SharedContributionReceipt(items.Count, 0, epoch));
        }

        public Task<SharedInstallation> RegisterAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<SharedConsentState> GetConsentAsync(SharedInstallation installation, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<SharedConsentState> UpdateConsentAsync(SharedInstallation installation, SharedConsentUpdate update, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<SharedQueryResponse> QueryAsync(SharedInstallation installation, long epoch, IReadOnlyList<SharedFingerprint> items, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<SharedContributionList> ListContributionsAsync(SharedInstallation installation, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<SharedWithdrawalReceipt> WithdrawAsync(SharedInstallation installation, long epoch, string? contributionId = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task FeedbackAsync(SharedInstallation installation, long epoch, string key, string reason, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}
