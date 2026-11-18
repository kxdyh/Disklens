using System.Net.Http.Headers;
using System.Text.Json;

namespace DiskLens.Core.SharedKnowledge;

public interface ISharedExperienceClient
{
    Task<SharedInstallation> RegisterAsync(CancellationToken cancellationToken = default);
    Task<SharedConsentState> GetConsentAsync(SharedInstallation installation, CancellationToken cancellationToken = default);
    Task<SharedConsentState> UpdateConsentAsync(SharedInstallation installation, SharedConsentUpdate update, CancellationToken cancellationToken = default);
    Task<SharedQueryResponse> QueryAsync(SharedInstallation installation, long epoch, IReadOnlyList<SharedFingerprint> items, CancellationToken cancellationToken = default);
    Task<SharedQueryResponse> QueryAsync(SharedInstallation installation, long epoch, IReadOnlyList<SharedFingerprint> items,
        CancellationToken cancellationToken, bool retryTransientFailure)
        => QueryAsync(installation, epoch, items, cancellationToken);
    Task<SharedPathIndex?> GetSharedPathIndexAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<SharedPathIndex?>(null);
    Task<SharedPathIndex?> RefreshSharedPathIndexAsync(CancellationToken cancellationToken = default)
        => GetSharedPathIndexAsync(cancellationToken);
    Task<SharedPathIndex?> GetSharedPathIndexAsync(CancellationToken cancellationToken, bool retryTransientFailure)
        => GetSharedPathIndexAsync(cancellationToken);
    Task<SharedPathIndex?> RefreshSharedPathIndexAsync(CancellationToken cancellationToken, bool retryTransientFailure)
        => RefreshSharedPathIndexAsync(cancellationToken);
    Task<SharedAnnouncementFeed> GetAnnouncementsAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(SharedAnnouncementFeed.Empty);
    Task<SharedQueryResponse> QueryOwnObservationsAsync(SharedInstallation installation, long epoch,
        IReadOnlyList<SharedFingerprint> items, CancellationToken cancellationToken = default)
        => Task.FromResult(new SharedQueryResponse([]));
    Task<SharedContributionReceipt> ContributeAsync(SharedInstallation installation, long epoch, IReadOnlyList<SharedContribution> items, CancellationToken cancellationToken = default);
    Task<SharedContributionList> ListContributionsAsync(SharedInstallation installation, CancellationToken cancellationToken = default);
    Task<SharedWithdrawalReceipt> WithdrawAsync(SharedInstallation installation, long epoch, string? contributionId = null, CancellationToken cancellationToken = default);
    Task FeedbackAsync(SharedInstallation installation, long epoch, string key, string reason, CancellationToken cancellationToken = default);
}

/// <summary>Short, bounded, nonredirecting requests. Tokens are never placed in URLs, logs, or exceptions.</summary>
public sealed class SharedExperienceClient : ISharedExperienceClient, IDisposable
{
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(25);
    public event Action<SharedRequestDiagnostic>? RequestCompleted;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly SharedInitialConnectionRoute? _initialConnectionRoute;
    private readonly SemaphoreSlim _pathIndexGate = new(1, 1);
    private SharedPathIndex? _pathIndex;
    // A 500-item management page can contain four complete purpose texts.
    // Keep transport bounded without truncating a valid full bilingual page.
    private const int MaximumResponseBytes = 1024 * 1024;
    public SharedExperienceClient(Uri endpoint) : this(endpoint, CreateDefaultHandler(endpoint)) { }
    public SharedExperienceClient(Uri endpoint, HttpMessageHandler handler)
    {
        ValidateEndpoint(endpoint);
        _initialConnectionRoute = handler as SharedInitialConnectionRoute;
        _http = new HttpClient(handler) { BaseAddress = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/"), Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DiskLens", "1.1.0"));
    }

    private static HttpMessageHandler CreateDefaultHandler(Uri endpoint)
    {
        // Reject an invalid endpoint before a route owns NetworkChange subscriptions.
        ValidateEndpoint(endpoint);
        return new SharedInitialConnectionRoute(endpoint);
    }

    private static void ValidateEndpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri || !(endpoint.Scheme == "https" || endpoint.Scheme == "http" && endpoint.IsLoopback)
            || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment))
            throw new ArgumentException("共享经验服务地址必须使用 HTTPS，隔离测试允许本机 HTTP。", nameof(endpoint));
    }
    public async Task<SharedInstallation> RegisterAsync(CancellationToken cancellationToken = default)
    {
        var installation = await SendAsync<SharedInstallation>(HttpMethod.Post, "v1/install", new { }, null, cancellationToken).ConfigureAwait(false);
        if (!Guid.TryParse(installation.InstallationId, out _) || installation.Epoch != 0
            || !System.Text.RegularExpressions.Regex.IsMatch(installation.ManagementToken ?? "", "^[A-Za-z0-9_-]{43}$"))
            throw new InvalidDataException("共享经验安装凭据格式无效。");
        return installation;
    }
    public Task<SharedConsentState> GetConsentAsync(SharedInstallation installation, CancellationToken cancellationToken = default) => SendReadAsync<SharedConsentState>(HttpMethod.Get, "v1/consent", null, installation, cancellationToken);
    public Task<SharedConsentState> UpdateConsentAsync(SharedInstallation installation, SharedConsentUpdate update, CancellationToken cancellationToken = default) => SendAsync<SharedConsentState>(HttpMethod.Put, "v1/consent", update, installation, cancellationToken);
    public Task<SharedQueryResponse> QueryAsync(SharedInstallation installation, long epoch, IReadOnlyList<SharedFingerprint> items, CancellationToken cancellationToken = default)
        => QueryAsync(installation, epoch, items, cancellationToken, retryTransientFailure: true);

    public Task<SharedQueryResponse> QueryAsync(SharedInstallation installation, long epoch, IReadOnlyList<SharedFingerprint> items,
        CancellationToken cancellationToken, bool retryTransientFailure)
    {
        if (items.Count > 25) throw new ArgumentOutOfRangeException(nameof(items));
        if (items.Any(item => !SharedFingerprintProtocol.IsValid(item))) throw new ArgumentException("查询项目不符合匿名路径格式。", nameof(items));
        return SendReadAsync<SharedQueryResponse>(HttpMethod.Post, "v1/query", new { epoch, items }, installation,
            cancellationToken, retryTransientFailure);
    }
    public Task<SharedPathIndex?> GetSharedPathIndexAsync(CancellationToken cancellationToken = default)
        => GetSharedPathIndexAsync(cancellationToken, retryTransientFailure: true);
    public Task<SharedPathIndex?> RefreshSharedPathIndexAsync(CancellationToken cancellationToken = default)
        => RefreshSharedPathIndexAsync(cancellationToken, retryTransientFailure: true);
    public Task<SharedPathIndex?> GetSharedPathIndexAsync(CancellationToken cancellationToken, bool retryTransientFailure)
        => LoadSharedPathIndexAsync(forceRefresh: false, cancellationToken, retryTransientFailure);
    public Task<SharedPathIndex?> RefreshSharedPathIndexAsync(CancellationToken cancellationToken, bool retryTransientFailure)
        => LoadSharedPathIndexAsync(forceRefresh: true, cancellationToken, retryTransientFailure);
    public async Task<SharedAnnouncementFeed> GetAnnouncementsAsync(CancellationToken cancellationToken = default)
    {
        // Public reading uses the same route, deadlines and diagnostics as cloud
        // experience requests, without registering or sending installation tokens.
        var feed = await SendReadAsync<SharedAnnouncementFeed>(HttpMethod.Get, "v1/announcements", null, null,
            cancellationToken).ConfigureAwait(false);
        if (!feed.IsValid()) throw new InvalidDataException("公告响应格式不正确。");
        return feed;
    }
    private async Task<SharedPathIndex?> LoadSharedPathIndexAsync(bool forceRefresh, CancellationToken cancellationToken,
        bool retryTransientFailure)
    {
        if (!forceRefresh && _pathIndex is { IsUsable: true } cached) return cached;
        await _pathIndexGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && _pathIndex is { IsUsable: true } current) return current;
            var shapes = new HashSet<string>(StringComparer.Ordinal);
            var legacyKeys = new HashSet<string>(StringComparer.Ordinal);
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;
            do
            {
                var page = await SendReadAsync<SharedPathIndex>(HttpMethod.Get, "v1/path-index"
                    + (cursor is null ? "" : "?cursor=" + Uri.EscapeDataString(cursor)), null, null,
                    cancellationToken, retryTransientFailure).ConfigureAwait(false);
                if (page.Version != 1 || !page.Complete || page.Shapes is null
                    || (long)page.Shapes.Count + (page.LegacyKeys?.Count ?? 0) > 1000
                    || page.BuiltAt > DateTimeOffset.UtcNow.AddMinutes(1) || page.ExpiresAt <= DateTimeOffset.UtcNow
                    || page.ExpiresAt > page.BuiltAt.AddMinutes(5)
                    || page.Shapes.Any(shape => !System.Text.RegularExpressions.Regex.IsMatch(shape ?? "", "\\Apurpose3-shared-(directory|file)-[a-f0-9]{64}\\z"))
                    || (page.LegacyKeys?.Any(key => !System.Text.RegularExpressions.Regex.IsMatch(key ?? "", "\\A[a-f0-9]{64}\\z")) ?? false))
                    throw new InvalidDataException("共享路径索引无效或未完整加载。");
                foreach (var shape in page.Shapes) shapes.Add(shape);
                foreach (var key in page.LegacyKeys ?? []) legacyKeys.Add(key);
                if ((long)shapes.Count + legacyKeys.Count > 2_000_000) throw new InvalidDataException("共享路径索引超过客户端容量。");
                cursor = page.NextCursor;
                if (cursor is not null && (cursor.Length > 512 || !cursors.Add(cursor)))
                    throw new InvalidDataException("共享路径索引分页无效。");
            } while (cursor is not null);
            // Each page was validated on arrival. A long paged download does not
            // inherit the first page's short cache deadline as a total time limit.
            var completedAt = DateTimeOffset.UtcNow;
            var index = new SharedPathIndex(1, shapes.ToArray(), true, completedAt,
                completedAt.AddMinutes(1), LegacyKeys: legacyKeys.ToArray());
            if (!index.IsUsable) throw new InvalidDataException("共享路径索引已过期。");
            _pathIndex = index;
            return index;
        }
        finally { _pathIndexGate.Release(); }
    }
    public Task<SharedQueryResponse> QueryOwnObservationsAsync(SharedInstallation installation, long epoch,
        IReadOnlyList<SharedFingerprint> items, CancellationToken cancellationToken = default)
    {
        if (items.Count is < 1 or > 25) throw new ArgumentOutOfRangeException(nameof(items));
        if (items.Any(item => !SharedFingerprintProtocol.IsOwnObservation(item)))
            throw new ArgumentException("本人云端恢复仅接受本机随机观察身份。", nameof(items));
        return SendAsync<SharedQueryResponse>(HttpMethod.Post, "v1/observations/query", new { epoch, items }, installation, cancellationToken);
    }
    public async Task<SharedContributionReceipt> ContributeAsync(SharedInstallation installation, long epoch, IReadOnlyList<SharedContribution> items, CancellationToken cancellationToken = default)
    {
        if (items.Count > 10) throw new ArgumentOutOfRangeException(nameof(items));
        if (items.Any(item => !SharedFingerprintProtocol.IsValid(new(item.SoftwareId, item.VersionMajor, item.RoleId, item.Shape))
            || !SharedFingerprintProtocol.ValidConclusion(item.Purpose, item.Action)
            || !SharedPurposeText.IsValidPair(item.ShortPurpose, item.Reason)
            || !SharedPurposeText.IsValidEnglishPair(item.ShortPurposeEn, item.ReasonEn)
            || item.ShortPurposeEn.Length > 0 && item.ShortPurpose.Length == 0
            || item.ConfidenceLevel is not ("Medium" or "High")
            || item.Source is not ("fresh-ai" or "historical-ai") || item.ProviderClass is not ("official" or "custom")
            || item.ModelFamily is not ("deepseek" or "openai" or "ollama" or "other")
            || !System.Text.RegularExpressions.Regex.IsMatch(item.AnalysisId, "^[A-Za-z0-9_-]{8,80}$")))
            throw new ArgumentException("共享内容不符合受控字段规则。", nameof(items));
        // Missing legacy purpose text must stay absent on the wire. This keeps
        // category-only uploads compatible with older strict server schemas.
        var wireItems = items.Select(item =>
        {
            var fields = new Dictionary<string, object>
            {
                ["analysisId"] = item.AnalysisId, ["softwareId"] = item.SoftwareId,
                ["versionMajor"] = item.VersionMajor, ["roleId"] = item.RoleId, ["shape"] = item.Shape,
                ["purpose"] = item.Purpose, ["action"] = item.Action, ["source"] = item.Source,
                ["providerClass"] = item.ProviderClass, ["modelFamily"] = item.ModelFamily,
                ["defaultPrompt"] = item.DefaultPrompt, ["sharedContext"] = item.SharedContext,
                ["confidenceLevel"] = item.ConfidenceLevel,
            };
            if (item.ShortPurpose.Length > 0)
            {
                fields["shortPurpose"] = item.ShortPurpose;
                fields["reason"] = item.Reason;
            }
            if (item.ShortPurposeEn.Length > 0)
            {
                fields["shortPurposeEn"] = item.ShortPurposeEn;
                fields["reasonEn"] = item.ReasonEn;
            }
            return fields;
        }).ToArray();
        var receipt = await SendAsync<SharedContributionReceipt>(HttpMethod.Post, "v1/contributions", new { epoch, items = wireItems }, installation, cancellationToken).ConfigureAwait(false);
        _pathIndex = null;
        return receipt;
    }
    public async Task<SharedContributionList> ListContributionsAsync(SharedInstallation installation, CancellationToken cancellationToken = default)
    {
        var initial = await GetConsentAsync(installation, cancellationToken).ConfigureAwait(false);
        var entries = new List<SharedContributionItem>();
        string? cursor = null; var seen = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = await SendAsync<SharedContributionList>(HttpMethod.Get,
                "v1/contributions" + (cursor is null ? "" : "?cursor=" + cursor), null, installation, cancellationToken).ConfigureAwait(false);
            if (response.Items is null || response.Items.Count > 500) throw new InvalidDataException("共享记录格式无效。");
            var lastId = cursor;
            foreach (var item in response.Items)
            {
                if (item is null || !System.Text.RegularExpressions.Regex.IsMatch(item.Id ?? "", "^[a-f0-9]{64}$")
                    || lastId is not null && string.CompareOrdinal(item.Id, lastId) <= 0)
                    throw new InvalidDataException("共享记录分页无效。");
                lastId = item.Id;
            }
            entries.AddRange(response.Items);
            if (response.NextCursor is null)
            {
                var current = await GetConsentAsync(installation, cancellationToken).ConfigureAwait(false);
                if (current.Epoch != initial.Epoch) throw new InvalidDataException("共享状态已变化，请重新加载记录。");
                return new(entries);
            }
            var nextCursor = response.NextCursor;
            if (!System.Text.RegularExpressions.Regex.IsMatch(nextCursor, "^[a-f0-9]{64}$")
                || response.Items.Count == 0 || nextCursor != lastId
                || cursor is not null && string.CompareOrdinal(nextCursor, cursor) <= 0 || !seen.Add(nextCursor))
                throw new InvalidDataException("共享记录分页无效。");
            cursor = nextCursor;
        }
    }
    public async Task<SharedWithdrawalReceipt> WithdrawAsync(SharedInstallation installation, long epoch,
        string? contributionId = null, CancellationToken cancellationToken = default)
    {
        var receipt = await SendAsync<SharedWithdrawalReceipt>(HttpMethod.Delete,
            "v1/contributions" + (contributionId is null ? "" : "/" + Uri.EscapeDataString(contributionId))
            + "?epoch=" + epoch.ToString(System.Globalization.CultureInfo.InvariantCulture), null, installation, cancellationToken).ConfigureAwait(false);
        if (receipt.Epoch != checked(epoch + 1)) throw new InvalidDataException("共享撤回回执无效。");
        // The owner has explicitly requested deletion. The server first makes
        // every old contribution unusable, then physically clears bounded pages.
        // No scheduled cleanup or experience age limit is involved.
        while (contributionId is null && receipt.CleanupPending && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                var next = await SendAsync<SharedWithdrawalReceipt>(HttpMethod.Delete,
                    "v1/contributions/cleanup?epoch=" + receipt.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    null, installation, cancellationToken).ConfigureAwait(false);
                if (next.Epoch != receipt.Epoch || next.CleanupDeleted is < 0 or > 200)
                    throw new InvalidDataException("共享撤回回执无效。");
                receipt = next;
                if (receipt.CleanupPending && receipt.CleanupDeleted == 0) break;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException)
            {
                // Revocation has already succeeded; preserve the new epoch so
                // a quota/network pause cannot undo it or look like a failure.
                break;
            }
        }
        return receipt;
    }
    public async Task FeedbackAsync(SharedInstallation installation, long epoch, string key, string reason, CancellationToken cancellationToken = default)
    {
        if (reason is not ("wrong-owner" or "wrong-role" or "unsafe-advice" or "outdated")) throw new ArgumentException("未知反馈类型。", nameof(reason));
        await SendAsync<JsonElement>(HttpMethod.Post, "v1/feedback", new { epoch, key, reason }, installation, cancellationToken).ConfigureAwait(false);
    }
    private async Task<T> SendReadAsync<T>(HttpMethod method, string path, object? body,
        SharedInstallation? installation, CancellationToken cancellationToken, bool retryTransientFailure = true)
    {
        // Only explicitly read-only operations use this retry boundary.
        // A write whose response was lost must never be replayed here.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await SendAsync<T>(method, path, body, installation, cancellationToken,
                    DefaultRequestTimeout).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt == 0 && !cancellationToken.IsCancellationRequested
                && IsTransientReadFailure(ex))
            {
                _initialConnectionRoute?.RefreshAfterReadFailure(ex);
                if (!retryTransientFailure) throw;
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransientReadFailure(Exception exception) => exception switch
    {
        HttpRequestException http => http.StatusCode is null || (int)http.StatusCode.Value == 408
            || (int)http.StatusCode.Value is >= 500 and <= 599,
        OperationCanceledException => true,
        IOException when exception is not InvalidDataException => true,
        _ => false,
    };

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, SharedInstallation? installation,
        CancellationToken cancellationToken, TimeSpan? requestTimeout = null)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var operation = GetDiagnosticOperation(method, path);
        var phase = "Routing";
        var outcome = "Failure";
        int? status = null;
        var timeout = requestTimeout ?? DefaultRequestTimeout;
        CancellationTokenSource? deadline = null;
        try
        {
            // The five-second probe chooses a route using a read-only request.
            // The business deadline starts afterwards; writes are never replayed.
            if (_initialConnectionRoute is not null)
                await _initialConnectionRoute.EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
            deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            var deadlineToken = deadline.Token;
            deadlineToken.ThrowIfCancellationRequested();
            phase = "Sending";
            using var request = new HttpRequestMessage(method, path);
            if (installation is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", installation.ManagementToken);
                request.Headers.Add("X-Installation-Id", installation.InstallationId);
            }
            if (body is not null)
            {
                var data = JsonSerializer.SerializeToUtf8Bytes(body, Json);
                deadlineToken.ThrowIfCancellationRequested();
                if (data.Length > 128 * 1024) throw new InvalidOperationException("共享经验请求超过大小限制。");
                request.Content = new ByteArrayContent(data);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadlineToken).ConfigureAwait(false);
            status = (int)response.StatusCode;
            // Only this fixed 503 code has user-facing meaning. No remote text
            // is retained in exceptions, logs or UI.
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable
                    && await IsNewContributorsPausedAsync(response.Content, deadlineToken).ConfigureAwait(false))
                    throw new SharedNewContributorsPausedException();
                throw new HttpRequestException("共享经验服务暂时不可用，请稍后重试。", null, response.StatusCode);
            }
            if (response.Content.Headers.ContentLength > MaximumResponseBytes || response.Content.Headers.ContentType?.MediaType != "application/json")
                throw new InvalidDataException("共享经验服务返回格式不正确。");
            phase = "Reading";
            await using var stream = await response.Content.ReadAsStreamAsync(deadlineToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            while (true)
            {
                var count = await stream.ReadAsync(bytes, deadlineToken).ConfigureAwait(false);
                if (count == 0) break;
                if (buffer.Length + count > MaximumResponseBytes) throw new InvalidDataException("共享经验响应超过大小限制。");
                buffer.Write(bytes, 0, count);
            }
            phase = "Validating";
            buffer.Position = 0;
            var result = await JsonSerializer.DeserializeAsync<T>(buffer, Json, deadlineToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("共享经验服务返回空内容。");
            phase = "Completed";
            outcome = "Success";
            return result;
        }
        catch (OperationCanceledException ex)
        {
            if (cancellationToken.IsCancellationRequested) outcome = "CallerCanceled";
            else if (deadline?.IsCancellationRequested == true)
            {
                outcome = "DeadlineExceeded";
                throw new SharedRequestTimeoutException(operation, phase, timeout, watch.Elapsed, ex, deadline.Token);
            }
            else outcome = "TransportCanceled";
            throw;
        }
        catch (SharedNewContributorsPausedException)
        {
            outcome = "NewContributorsPaused";
            throw;
        }
        catch (HttpRequestException ex)
        {
            status ??= (int?)ex.StatusCode;
            outcome = status.HasValue ? "HttpFailure" : "TransportFailure";
            throw;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException)
        {
            outcome = "InvalidResponse";
            throw;
        }
        catch (IOException)
        {
            outcome = "TransportFailure";
            throw;
        }
        finally
        {
            deadline?.Dispose();
            ReportRequestCompleted(new(operation, phase, outcome, status, watch.Elapsed, timeout));
        }
    }
    private static async Task<bool> IsNewContributorsPausedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        const int maximumErrorBytes = 2048;
        if (content.Headers.ContentLength > maximumErrorBytes
            || !string.Equals(content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)) return false;
        await using var input = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var bytes = new byte[maximumErrorBytes];
        var length = 0;
        while (length < bytes.Length)
        {
            var count = await input.ReadAsync(bytes.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            length += count;
        }
        // An unknown or oversized body must never extend this bounded read.
        if (length == 0 || (length == maximumErrorBytes && content.Headers.ContentLength != length)) return false;
        try
        {
            using var document = JsonDocument.Parse(bytes.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 4 });
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                && error.GetString() == "new_contributors_paused";
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return false; }
    }

    private static string GetDiagnosticOperation(HttpMethod method, string path)
    {
        // Only fixed labels leave this boundary. In particular, management IDs
        // and paging cursors in a path/query must never reach diagnostics.
        var query = path.IndexOf('?');
        var route = query < 0 ? path : path[..query];
        if (method == HttpMethod.Post && route == "v1/install") return "Register";
        if (method == HttpMethod.Get && route == "v1/consent") return "ReadConsent";
        if (method == HttpMethod.Put && route == "v1/consent") return "UpdateConsent";
        if (method == HttpMethod.Get && route == "v1/path-index") return "PathIndex";
        if (method == HttpMethod.Get && route == "v1/announcements") return "Announcements";
        if (method == HttpMethod.Post && route == "v1/query") return "Query";
        if (method == HttpMethod.Post && route == "v1/contributions") return "Contribute";
        if (method == HttpMethod.Get && route == "v1/contributions") return "ListContributions";
        if (method == HttpMethod.Delete && route == "v1/contributions/cleanup") return "Cleanup";
        if (method == HttpMethod.Delete && (route == "v1/contributions"
            || route.StartsWith("v1/contributions/", StringComparison.Ordinal))) return "Withdraw";
        if (method == HttpMethod.Post && route == "v1/feedback") return "Feedback";
        if (method == HttpMethod.Post && route == "v1/observations/query") return "OwnObservations";
        if (method == HttpMethod.Post && route == "v1/diagnostics/contribution") return "Diagnostic";
        return "Other";
    }

    private void ReportRequestCompleted(SharedRequestDiagnostic diagnostic)
    {
        var subscribers = RequestCompleted;
        if (subscribers is null) return;
        foreach (Action<SharedRequestDiagnostic> subscriber in subscribers.GetInvocationList())
        {
            try { subscriber(diagnostic); }
            catch (Exception) { /* Optional diagnostics must not fail or retry a business request. */ }
        }
    }

    public void Dispose() => _http.Dispose();

}
