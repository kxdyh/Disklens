using System.IO;
using System.Net.Http;
using System.Text.Json;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.App.Services;

internal sealed record AnnouncementRefreshResult(bool Succeeded, bool HasCachedContent);

/// <summary>Retains the last complete public feed when a later network request fails.</summary>
internal sealed class AnnouncementStore(ISharedExperienceClient client, string cachePath)
{
    private const int MaximumCacheBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CacheDocument _document = new(1, SharedAnnouncementFeed.Empty, null, new Dictionary<string, int>());
    private bool _loaded;
    private DateTimeOffset _lastSuccess;
    public event EventHandler? Changed;
    public SharedAnnouncementFeed Feed => _document.Feed;
    public DateTimeOffset? FetchedAt => _document.FetchedAt;
    public bool HasUnread => _document.Feed.Items.Any(item =>
        !_document.Seen.TryGetValue(item.Id, out var revision) || revision < item.Revision);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(cachePath) || new FileInfo(cachePath).Length > MaximumCacheBytes) return;
                var bytes = await File.ReadAllBytesAsync(cachePath, cancellationToken).ConfigureAwait(false);
                var document = JsonSerializer.Deserialize<CacheDocument>(bytes, Json);
                if (document is { SchemaVersion: 1, Feed: not null, Seen: not null }
                    && document.Feed.IsValid() && document.Seen.Count <= 50
                    && document.FetchedAt <= DateTimeOffset.UtcNow.AddMinutes(5))
                    _document = document;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<AnnouncementRefreshResult> RefreshAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var succeeded = false;
        try
        {
            // Startup, manual opening and polling can overlap. Share a recently
            // completed read rather than issuing duplicate simultaneous requests.
            if (DateTimeOffset.UtcNow - _lastSuccess < TimeSpan.FromSeconds(2))
                return new(true, _document.FetchedAt is not null);
            try
            {
                var feed = await client.GetAnnouncementsAsync(cancellationToken).ConfigureAwait(false);
                if (!feed.IsValid()) throw new InvalidDataException("公告响应格式不正确。");
                var ordered = feed.Items.OrderByDescending(item => item.Priority)
                    .ThenByDescending(item => item.PublishedAt).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();
                var retainedSeen = _document.Seen.Where(pair => ordered.Any(item => item.Id == pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                _document = new(1, feed with { Items = ordered }, DateTimeOffset.UtcNow, retainedSeen);
                _lastSuccess = DateTimeOffset.UtcNow;
                succeeded = true;
                await SaveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException
                or IOException or UnauthorizedAccessException or JsonException) { }
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
        return new(succeeded, _document.FetchedAt is not null);
    }

    public async Task MarkReadAsync(SharedAnnouncement item, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var changed = false;
        try
        {
            if (_document.Seen.TryGetValue(item.Id, out var revision) && revision >= item.Revision) return;
            if (!_document.Feed.Items.Any(current => current.Id == item.Id && current.Revision == item.Revision)) return;
            var seen = new Dictionary<string, int>(_document.Seen, StringComparer.Ordinal) { [item.Id] = item.Revision };
            _document = _document with { Seen = seen };
            changed = true;
            await SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        var temporary = cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(_document, Json);
            if (bytes.Length > MaximumCacheBytes) return;
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, cachePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private sealed record CacheDocument(int SchemaVersion, SharedAnnouncementFeed Feed,
        DateTimeOffset? FetchedAt, Dictionary<string, int> Seen);
}
