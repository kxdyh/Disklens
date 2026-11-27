using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DiskLens.App.Services.SharedKnowledge;
using DiskLens.Core.Models;
using DiskLens.Core.Networking;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.App.Tests;

/// <summary>Real encrypted durable outbox; all service calls are in-memory fakes.</summary>
public sealed partial class SharedContributionOutboxBehaviorTests : IDisposable
{
    private readonly string _directory;
    private readonly ManualClock _clock = new();
    private readonly ConsentSource _consent = new();
    private readonly RecordingClient _client = new();

    public SharedContributionOutboxBehaviorTests()
    {
        var artifacts = Environment.GetEnvironmentVariable("DISKLENS_TEST_ARTIFACTS")
            ?? Path.Combine(Path.GetTempPath(), "DiskLens.Outbox.Tests");
        _directory = Path.Combine(artifacts, "outbox-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _consent.Value = Active(_clock.GetUtcNow());
    }

    [Fact]
    public async Task Pending_upload_survives_restart_and_reuses_original_analysis_id()
    {
        var expected = Queued("original-analysis-0001");
        using (var first = Create())
        {
            await first.EnqueueAsync(_consent.Value, [expected]);
            Assert.Equal(1, await first.PendingCountAsync());
            Assert.Empty(_client.Batches);
        }

        using var restarted = Create();
        var receipt = await restarted.RetryPendingAsync();

        Assert.Equal(1, receipt.Accepted);
        Assert.Equal(expected.Contribution, Assert.Single(Assert.Single(_client.Batches)));
        Assert.Equal(0, await restarted.PendingCountAsync());
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task Lost_response_retries_same_payload_and_duplicate_receipt_acknowledges_it()
    {
        var expected = Queued("lost-response-analysis");
        _client.Response = (_, _, call) => call == 1
            ? throw new HttpRequestException("synthetic response loss")
            : new(0, 1, _consent.Value.Epoch);
        using var outbox = Create();
        await outbox.EnqueueAsync(_consent.Value, [expected]);

        await outbox.FlushAsync();
        Assert.Equal(1, await outbox.PendingCountAsync());
        _clock.Advance(TimeSpan.FromDays(1));
        var receipt = await outbox.RetryPendingAsync();

        Assert.Equal(2, _client.Batches.Count);
        Assert.Equal(_client.Batches[0], _client.Batches[1]);
        Assert.Equal(1, receipt.Skipped);
        Assert.Equal(0, await outbox.PendingCountAsync());
    }

    [Fact]
    public async Task Original_dual_purpose_survives_encrypted_restart_and_offline_retry_without_rewriting()
    {
        var expected = Queued("dual-purpose-original") with
        {
            Contribution = Queued("dual-purpose-original").Contribution with
            {
                ShortPurpose = "应用临时缓存", Reason = "应用生成的临时缓存，可在关闭程序后清理",
            }
        };
        _client.Response = (_, _, call) => call == 1
            ? throw new HttpRequestException("synthetic offline")
            : new(1, 0, _consent.Value.Epoch);
        using (var original = Create())
        {
            await original.EnqueueAsync(_consent.Value, [expected]);
            Assert.Equal(1, (await original.FlushAsync()).Pending);
        }
        _clock.Advance(TimeSpan.FromDays(1));
        using var restarted = Create();
        Assert.Equal(1, (await restarted.RetryPendingAsync()).Accepted);
        Assert.Equal(2, _client.Batches.Count);
        Assert.All(_client.Batches, batch => Assert.Equal(expected.Contribution, Assert.Single(batch)));
        Assert.Equal(0, await restarted.PendingCountAsync());
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("scope")]
    [InlineData("policy")]
    [InlineData("identity")]
    [InlineData("epoch")]
    [InlineData("agreement")]
    [InlineData("history")]
    public async Task Consent_changes_clear_queued_work_and_reenabling_does_not_resurrect_it(string change)
    {
        using var outbox = Create();
        var original = _consent.Value;
        await outbox.EnqueueAsync(original, [Queued("invalidated-analysis")]);
        _consent.Value = change switch
        {
            "disabled" => original with { Enabled = false },
            "scope" => original with { ExcludedPaths = [@"C:\Users\private-person\secrets"] },
            "policy" => original with { PolicyRevision = "revised-policy" },
            "identity" => original with { Installation = new("different-installation", "synthetic-different-token", 7) },
            "epoch" => original with { Epoch = 8 },
            "agreement" => original with { AgreementVersion = "old-agreement" },
            _ => original with { ShareHistory = true }
        };

        await outbox.SynchronizeConsentAsync();
        Assert.Equal(0, await outbox.PendingCountAsync());
        _consent.Value = original with { Epoch = 9, PolicyRevision = "new-explicit-opt-in", EnabledAt = _clock.GetUtcNow() };
        await outbox.SynchronizeConsentAsync();
        await outbox.FlushAsync();

        Assert.Empty(_client.Batches);
        using var restarted = Create();
        Assert.Equal(0, await restarted.PendingCountAsync());
    }

    [Theory]
    [InlineData("wrong-epoch")]
    [InlineData("missing-count")]
    [InlineData("overflow-count")]
    [InlineData("negative-count")]
    public async Task Malformed_receipt_never_removes_unacknowledged_work(string kind)
    {
        using var outbox = Create();
        await outbox.EnqueueAsync(_consent.Value, [Queued("unacknowledged-analysis")]);
        _client.Response = (_, epoch, _) => kind switch
        {
            "wrong-epoch" => new(1, 0, epoch + 1),
            "missing-count" => new(0, 0, epoch),
            "overflow-count" => new(1, 1, epoch),
            _ => new(-1, 2, epoch)
        };

        await outbox.FlushAsync();

        Assert.Equal(1, await outbox.PendingCountAsync());
    }

    [Fact]
    public async Task Durable_payload_contains_only_anonymous_controlled_fields_even_after_DPAPI_decryption()
    {
        const string privatePath = @"C:\Users\private-person\private-project\secret-notes.docx";
        _consent.Value = _consent.Value with { ExcludedPaths = [privatePath] };
        using var outbox = Create();
        await outbox.EnqueueAsync(_consent.Value, [Queued("anonymous-analysis")]);

        var file = Assert.Single(Directory.EnumerateFiles(_directory));
        var encrypted = await File.ReadAllBytesAsync(file);
        Assert.InRange(encrypted.Length, 1, 1_048_576);
        var ciphertext = Encoding.UTF8.GetString(encrypted);
        Assert.DoesNotContain("anonymous-analysis", ciphertext, StringComparison.Ordinal);
        Assert.DoesNotContain(_consent.Value.Installation!.ManagementToken, ciphertext, StringComparison.Ordinal);
        var plaintext = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        try
        {
            var json = Encoding.UTF8.GetString(plaintext);
            using var document = JsonDocument.Parse(plaintext);
            Assert.DoesNotContain(privatePath, json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private-person", json, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(_consent.Value.Installation.ManagementToken, json, StringComparison.Ordinal);
            AssertNoPrivateFields(document.RootElement);
            Assert.Contains("anonymous-analysis", json, StringComparison.Ordinal);
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    [Theory]
    [InlineData("software")]
    [InlineData("purpose")]
    [InlineData("action")]
    [InlineData("provider")]
    [InlineData("model")]
    [InlineData("source")]
    public async Task Arbitrary_free_text_is_rejected_before_persistence_or_upload(string field)
    {
        var queued = Queued("invalid-controlled-field");
        const string secret = @"C:\Users\private-person\my-api-key-is-not-a-catalog-value";
        var invalid = queued.Contribution;
        invalid = field switch
        {
            "software" => invalid with { SoftwareId = secret },
            "purpose" => invalid with { Purpose = secret },
            "action" => invalid with { Action = secret },
            "provider" => invalid with { ProviderClass = secret },
            "model" => invalid with { ModelFamily = secret },
            _ => invalid with { Source = secret }
        };
        using var outbox = Create();

        // Either an argument rejection or a skipped entry is acceptable; neither may become durable work.
        var exception = await Record.ExceptionAsync(() => outbox.EnqueueAsync(_consent.Value,
            [queued with { Contribution = invalid }]));
        Assert.True(exception is null or ArgumentException or InvalidDataException,
            $"Unexpected rejection type: {exception?.GetType().Name}.");
        Assert.Equal(0, await outbox.PendingCountAsync());
        await outbox.FlushAsync();
        Assert.Empty(_client.Batches);
    }

    [Fact]
    public async Task Pending_analysis_does_not_expire_after_thirty_days()
    {
        _consent.Value = _consent.Value with { EnabledAt = _clock.GetUtcNow().AddDays(-40) };
        using (var first = Create())
        {
            await first.EnqueueAsync(_consent.Value,
                [Queued("durable-analysis", analyzedAt: _clock.GetUtcNow().AddDays(-29))]);
            Assert.Equal(1, await first.PendingCountAsync());
        }
        _clock.Advance(TimeSpan.FromDays(2));

        using var restarted = Create();
        Assert.Equal(1, await restarted.PendingCountAsync());
        await restarted.FlushAsync();

        Assert.Equal(0, await restarted.PendingCountAsync());
        Assert.Equal("durable-analysis", Assert.Single(Assert.Single(_client.Batches)).AnalysisId);
    }

    [Fact]
    public async Task Same_bucket_keeps_newest_analysis_and_older_enqueue_does_not_replace_it()
    {
        using var outbox = Create();
        var older = Queued("older-analysis", analyzedAt: _clock.GetUtcNow().AddMinutes(-2));
        var newer = Queued("newer-analysis", analyzedAt: _clock.GetUtcNow().AddMinutes(-1));
        await outbox.EnqueueAsync(_consent.Value, [older, newer]);
        await outbox.EnqueueAsync(_consent.Value, [older]);

        Assert.Equal(1, await outbox.PendingCountAsync());
        await outbox.FlushAsync();

        Assert.Equal("newer-analysis", Assert.Single(Assert.Single(_client.Batches)).AnalysisId);
    }

    [Fact]
    public async Task Old_inflight_ack_cannot_delete_newer_analysis_enqueued_for_same_bucket()
    {
        using var outbox = Create();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.BeforeResponse = async () => { started.TrySetResult(); await release.Task; };
        _client.Response = (items, epoch, call) => call == 1
            ? new(items.Count, 0, epoch)
            : throw new HttpRequestException("synthetic later request failure");
        await outbox.EnqueueAsync(_consent.Value, [Queued("old-inflight-analysis")]);
        var flush = outbox.FlushAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _clock.Advance(TimeSpan.FromSeconds(1));
        try
        {
            await outbox.EnqueueAsync(_consent.Value, [Queued("new-inflight-analysis")])
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.TrySetResult(); }
        await flush.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, await outbox.PendingCountAsync());
        _clock.Advance(TimeSpan.FromDays(1));
        _client.Response = null;
        await outbox.FlushAsync();
        Assert.Equal("new-inflight-analysis", Assert.Single(_client.Batches[^1]).AnalysisId);
        Assert.Equal(0, await outbox.PendingCountAsync());
    }

    [Fact]
    public async Task Retry_backoff_prevents_immediate_retries_and_survives_restart()
    {
        _client.Response = (_, _, _) => throw new HttpRequestException("synthetic offline");
        using (var first = Create())
        {
            await first.EnqueueAsync(_consent.Value, [Queued("backoff-analysis")]);
            await first.FlushAsync();
            await first.FlushAsync();
            Assert.Single(_client.Batches);
        }
        using var restarted = Create();
        await restarted.FlushAsync();
        Assert.Single(_client.Batches);

        _clock.Advance(TimeSpan.FromDays(1));
        _client.Response = null;
        await restarted.FlushAsync();

        Assert.Equal(2, _client.Batches.Count);
        Assert.Equal(0, await restarted.PendingCountAsync());
    }

    [Fact]
    public async Task Upload_retries_every_minute_at_most_three_times_and_persists_the_budget()
    {
        _client.Response = (_, _, _) => throw new HttpRequestException("synthetic transient failure");
        using var outbox = Create();
        await outbox.EnqueueAsync(_consent.Value, [Queued("bounded-retry-analysis")]);

        await outbox.FlushAsync();
        var status = await outbox.GetUploadStatusAsync();
        Assert.Equal(1, status.Pending);
        Assert.Equal(0, status.AutomaticRetriesUsed);
        Assert.Equal(_clock.GetUtcNow().AddMinutes(1), status.NextRetryAt);

        // No immediate retry, even when another background flush is requested.
        await outbox.FlushAsync();
        Assert.Single(_client.Batches);

        for (var retry = 1; retry <= 3; retry++)
        {
            _clock.Advance(TimeSpan.FromMinutes(1));
            await outbox.FlushAsync();
            status = await outbox.GetUploadStatusAsync();
            Assert.Equal(retry, status.AutomaticRetriesUsed);
            Assert.Equal(1, status.Pending);
            Assert.Equal(retry == 3, status.AutomaticRetriesExhausted);
            Assert.Equal(retry == 3 ? null : _clock.GetUtcNow().AddMinutes(1), status.NextRetryAt);
        }

        await outbox.FlushAsync();
        Assert.Equal(4, _client.Batches.Count); // initial send + exactly three retries
        Assert.Equal(1, await outbox.PendingCountAsync());
    }

    [Fact]
    public async Task Queue_accepts_more_than_512_anonymous_buckets_and_flushes_in_batches_of_ten()
    {
        using var outbox = Create();
        var work = Enumerable.Range(0, 513).Select(index =>
        {
            var item = Queued($"capacity-analysis-{index:D4}");
            var shape = "tree2-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(index.ToString())))
                .ToLowerInvariant();
            return item with { Contribution = item.Contribution with { Shape = shape } };
        }).ToArray();
        await outbox.EnqueueAsync(_consent.Value, work);
        Assert.Equal(513, await outbox.PendingCountAsync());

        await outbox.FlushAsync();

        Assert.Equal(513, _client.Batches.Sum(batch => batch.Length));
        Assert.Equal(52, _client.Batches.Count);
        Assert.All(_client.Batches, batch => Assert.InRange(batch.Length, 1, 10));
        Assert.Equal(0, await outbox.PendingCountAsync());
    }

    [Fact]
    public async Task Over_capacity_enqueue_preserves_the_previously_durable_queue()
    {
        using var outbox = Create();
        await outbox.EnqueueAsync(_consent.Value, [Queued("existing-durable-analysis")]);
        var additional = Enumerable.Range(0, 20_000).Select(index =>
        {
            var item = Queued($"overflow-analysis-{index:D4}");
            var shape = "tree2-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(index.ToString())))
                .ToLowerInvariant();
            return item with { Contribution = item.Contribution with { Shape = shape } };
        }).ToArray();

        await Assert.ThrowsAsync<InvalidDataException>(() => outbox.EnqueueAsync(_consent.Value, additional));

        Assert.Equal(1, await outbox.PendingCountAsync());
        await outbox.FlushAsync();
        Assert.Equal("existing-durable-analysis", Assert.Single(Assert.Single(_client.Batches)).AnalysisId);
    }

    [Fact]
    public async Task Consent_change_during_first_request_prevents_remaining_batches_and_queue_resurrection()
    {
        using var outbox = Create();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _client.BeforeResponse = async () => { started.TrySetResult(); await release.Task; };
        await outbox.EnqueueAsync(_consent.Value,
            Enumerable.Range(0, 12).Select(index => Queued($"midflight-analysis-{index:D2}", "0123456789ab"[index])).ToArray());
        var flush = outbox.FlushAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _consent.Value = _consent.Value with { Enabled = false, PolicyRevision = "privacy-scope-changed" };
        try { await outbox.SynchronizeConsentAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { release.TrySetResult(); }
        await flush.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(_client.Batches);
        Assert.Equal(0, await outbox.PendingCountAsync());
        _consent.Value = Active(_clock.GetUtcNow()) with { PolicyRevision = "new-opt-in" };
        await outbox.FlushAsync();
        Assert.Single(_client.Batches);
    }

    [Fact]
    public async Task Rate_limited_upload_uses_the_same_one_minute_bounded_retry_schedule()
    {
        using var outbox = Create();
        _client.Response = (_, _, _) => throw new HttpRequestException("synthetic rate limit", null, HttpStatusCode.TooManyRequests);
        await outbox.EnqueueAsync(_consent.Value, [Queued("rate-limited-analysis")]);
        await outbox.FlushAsync();
        _clock.Advance(TimeSpan.FromSeconds(59));
        await outbox.FlushAsync();
        Assert.Single(_client.Batches);

        _clock.Advance(TimeSpan.FromSeconds(1));
        _client.Response = null;
        await outbox.FlushAsync();

        Assert.Equal(2, _client.Batches.Count);
        Assert.Equal(0, await outbox.PendingCountAsync());
    }

    [Fact]
    public async Task Damaged_encrypted_queue_stays_paused_without_upload_or_automatic_registration()
    {
        using (var first = Create()) await first.EnqueueAsync(_consent.Value, [Queued("damaged-document-analysis")]);
        var file = Assert.Single(Directory.EnumerateFiles(_directory));
        await File.WriteAllBytesAsync(file, [1, 3, 5, 7, 9]);
        using var restarted = Create();

        var exception = await Record.ExceptionAsync(() => restarted.FlushAsync());

        Assert.Null(exception);
        Assert.Empty(_client.Batches);
        Assert.NotNull(restarted.LastError);
    }

    [Fact]
    public async Task Authenticated_but_invalid_document_stays_paused_without_upload()
    {
        using (var first = Create()) await first.EnqueueAsync(_consent.Value, [Queued("invalid-document-analysis")]);
        var file = Assert.Single(Directory.EnumerateFiles(_directory));
        var plain = ProtectedData.Unprotect(await File.ReadAllBytesAsync(file), null, DataProtectionScope.CurrentUser);
        try
        {
            var json = Encoding.UTF8.GetString(plain);
            using var parsed = JsonDocument.Parse(plain);
            var version = Assert.Single(parsed.RootElement.EnumerateObject(),
                property => property.Name.Equals("Version", StringComparison.OrdinalIgnoreCase));
            json = json.Replace($"\"{version.Name}\":1", $"\"{version.Name}\":999", StringComparison.Ordinal);
            Assert.Contains($"\"{version.Name}\":999", json, StringComparison.Ordinal);
            await File.WriteAllBytesAsync(file,
                ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser));
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
        using var restarted = Create();

        var exception = await Record.ExceptionAsync(() => restarted.FlushAsync());

        Assert.Null(exception);
        Assert.Empty(_client.Batches);
        Assert.NotNull(restarted.LastError);
    }

    [Fact]
    public async Task Duplicate_analysis_id_across_buckets_is_rejected_atomically()
    {
        using var outbox = Create();
        await Assert.ThrowsAsync<InvalidDataException>(() => outbox.EnqueueAsync(_consent.Value,
            [Queued("duplicate-analysis-id", 'a'), Queued("duplicate-analysis-id", 'b')]));
        Assert.Equal(0, await outbox.PendingCountAsync());
        await outbox.EnqueueAsync(_consent.Value, [Queued("existing-analysis-id", 'a')]);

        await Assert.ThrowsAsync<InvalidDataException>(() => outbox.EnqueueAsync(_consent.Value,
            [Queued("existing-analysis-id", 'b')]));

        Assert.Equal(1, await outbox.PendingCountAsync());
        await outbox.FlushAsync();
        Assert.EndsWith(new string('a', 64), Assert.Single(Assert.Single(_client.Batches)).Shape);
    }

    [Fact]
    public async Task Real_pipeline_durably_saves_all_normalized_items_before_first_HTTP_batch()
    {
        using var outbox = Create();
        var pipeline = new SharedKnowledgePipeline(_consent, new DistinctExtractor(), _client, _clock, outbox);
        var (candidates, results) = AnalysisInputs(23);
        var observedDurableTotal = 0;
        _client.BeforeResponse = async () =>
        {
            if (_client.Batches.Count != 1) return;
            using var restartedReader = Create();
            observedDurableTotal = await restartedReader.PendingCountAsync();
            Assert.Equal(23, observedDurableTotal);
        };

        var receipt = await pipeline.ContributeAsync(candidates, results,
            new("official", "openai", true) { ConsentAtAnalysisStart = _consent.Value });

        Assert.Equal(23, observedDurableTotal);
        Assert.Equal(3, _client.Batches.Count);
        Assert.Equal(23, receipt.Accepted);
        Assert.Equal(0, receipt.Failed);
        Assert.Equal(0, receipt.Pending);
    }

    [Fact]
    public async Task Real_pipeline_offline_upload_reports_pending_without_counting_enqueue_as_accepted()
    {
        using var outbox = Create();
        var pipeline = new SharedKnowledgePipeline(_consent, new DistinctExtractor(), _client, _clock, outbox);
        var (candidates, results) = AnalysisInputs(23);
        _client.Response = (_, _, _) => throw new HttpRequestException("synthetic offline");

        var receipt = await pipeline.ContributeAsync(candidates, results,
            new("official", "openai", true) { ConsentAtAnalysisStart = _consent.Value });

        Assert.Equal(0, receipt.Accepted);
        Assert.InRange(receipt.Failed, 1, 23);
        Assert.Equal(23, receipt.Pending);
        Assert.Equal(23, await outbox.PendingCountAsync());
        using var restartedReader = Create();
        Assert.Equal(23, await restartedReader.PendingCountAsync());
    }

    private (AiCandidateRecord[] Candidates, AiAnalysisResult[] Results) AnalysisInputs(int count)
    {
        var candidates = Enumerable.Range(0, count).Select(index =>
        {
            var path = $@"D:\private-person\cache-{index:D2}.tmp";
            var node = new FileSystemNode(path, $"cache-{index:D2}.tmp", ".tmp", 16, FileAttributes.Normal,
                null, null, false, false, [], []);
            return new AiCandidateRecord(path, node.Name, 16, false, 1, false, index.ToString(), node);
        }).ToArray();
        var results = candidates.Select((candidate, index) => new AiAnalysisResult(candidate.Path,
            RiskLevel.Review, SuggestedAction.Review, "private source text must not enter durable uploads", .9)
        {
            OriginalAnalysisId = $"pipeline-analysis-{index:D4}", OriginalAnalyzedAt = _clock.GetUtcNow(),
            ConfidenceLevel = AiConfidenceLevel.High, PurposeCategory = "cache"
        }).ToArray();
        return (candidates, results);
    }

    private static void AssertNoPrivateFields(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            // beta3 allows the two bounded original purpose texts after explicit
            // consent. Their presence does not permit paths or arbitrary prose.
            if (element.TryGetProperty("ShortPurpose", out var shortPurpose))
            {
                Assert.True(element.TryGetProperty("Reason", out var reason));
                Assert.True(SharedPurposeText.IsValidPair(shortPurpose.GetString(), reason.GetString()));
            }
            foreach (var property in element.EnumerateObject())
            {
                Assert.DoesNotContain(property.Name.ToLowerInvariant(), new[]
                {
                    "managementtoken", "apikey", "api_key", "excludedpaths", "path", "fullpath", "explanation"
                });
                AssertNoPrivateFields(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) AssertNoPrivateFields(item);
    }

    private SharedContributionOutbox Create(INetworkAvailability? networkAvailability = null)
        => new(_directory, _consent, _client, _clock, networkAvailability);

    private SharedQueuedContribution Queued(string id, char shape = 'a', DateTimeOffset? analyzedAt = null)
        => new(new(id, "generic-v2", "0", "path2:user-temp", "tree2-" + new string(shape, 64),
            "cache", "Review", "fresh-ai", "official", "openai", true, false, "High"),
            analyzedAt ?? _clock.GetUtcNow());

    private static SharedConsentSnapshot Active(DateTimeOffset now) => new(true,
        SharedConsentSnapshot.CurrentAgreementVersion, false, now.AddHours(-1), 7, "original-policy", [],
        new("synthetic-installation", "synthetic-management-token-not-for-queue", 7));

    public void Dispose()
    {
        // Preserve evidence for the root's unified test run; never deletes another task's files.
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    private sealed class ConsentSource : ISharedConsentSource
    {
        public SharedConsentSnapshot Value { get; set; } = null!;
        public Task<SharedConsentSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Value);
        }
    }

    private sealed class DistinctExtractor : ISharedFingerprintExtractor
    {
        public Task<SharedFingerprint?> ExtractAsync(AiCandidateRecord candidate, IReadOnlyList<string> exclusions,
            CancellationToken cancellationToken = default)
        {
            var shape = "tree2-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(candidate.Signature)))
                .ToLowerInvariant();
            return Task.FromResult<SharedFingerprint?>(new("generic-v2", "0", "path2:user-temp", shape));
        }
    }

    private sealed class RecordingClient : ISharedExperienceClient
    {
        public List<SharedContribution[]> Batches { get; } = [];
        public Func<IReadOnlyList<SharedContribution>, long, int, SharedContributionReceipt>? Response { get; set; }
        public Func<Task>? BeforeResponse { get; set; }

        public async Task<SharedContributionReceipt> ContributeAsync(SharedInstallation installation, long epoch,
            IReadOnlyList<SharedContribution> items, CancellationToken cancellationToken = default)
        {
            Batches.Add(items.ToArray());
            if (BeforeResponse is not null) await BeforeResponse();
            cancellationToken.ThrowIfCancellationRequested();
            return Response?.Invoke(items, epoch, Batches.Count) ?? new(items.Count, 0, epoch);
        }

        public Task<SharedInstallation> RegisterAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("Registration must remain explicit.");
        public Task<SharedConsentState> GetConsentAsync(SharedInstallation installation, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<SharedConsentState> UpdateConsentAsync(SharedInstallation installation, SharedConsentUpdate update, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<SharedQueryResponse> QueryAsync(SharedInstallation installation, long epoch, IReadOnlyList<SharedFingerprint> items, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<SharedContributionList> ListContributionsAsync(SharedInstallation installation, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<SharedWithdrawalReceipt> WithdrawAsync(SharedInstallation installation, long epoch, string? contributionId = null, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task FeedbackAsync(SharedInstallation installation, long epoch, string key, string reason, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
}
