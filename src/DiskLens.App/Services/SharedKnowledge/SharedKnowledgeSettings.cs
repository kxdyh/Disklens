using DiskLens.Core.SharedKnowledge;

namespace DiskLens.App.Services.SharedKnowledge;

/// <summary>Application-scoped coordination state for the optional beta feature.</summary>
public sealed class SharedKnowledgeSettings(SharedPreferenceStore store)
{
    private readonly SharedPreferenceStore _store = store;
    public event EventHandler? Changed;
    internal Func<Task>? BeforeConsentChangeAsync { get; set; }

    public Task<SharedPreferences> LoadAsync(CancellationToken cancellationToken = default)
        => _store.LoadAsync(cancellationToken);

    public async Task<bool> ShouldRecommendAsync(string appVersion, CancellationToken cancellationToken = default)
    {
        var settings = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        return !settings.Enabled && !string.Equals(settings.LastOfferedVersion, appVersion, StringComparison.OrdinalIgnoreCase);
    }

    public async Task MarkRecommendedAsync(string appVersion, CancellationToken cancellationToken = default)
    {
        var settings = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (string.Equals(settings.LastOfferedVersion, appVersion, StringComparison.OrdinalIgnoreCase)) return;
        await _store.SaveAsync(settings with { LastOfferedVersion = appVersion }, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(SharedPreferences settings, CancellationToken cancellationToken = default)
    {
        try
        {
            var previous = await _store.LoadAsync(cancellationToken);
            if (ConsentChanged(previous, settings) && BeforeConsentChangeAsync is not null)
            {
                try { await BeforeConsentChangeAsync(); }
                catch
                {
                    // If queue deletion fails, persist closed consent before returning the failure.
                    await _store.SuspendAsync(CancellationToken.None);
                    throw;
                }
            }
            await _store.SaveAsync(settings, cancellationToken);
        }
        finally { Changed?.Invoke(this, EventArgs.Empty); }
    }

    public async Task SuspendAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            try { if (BeforeConsentChangeAsync is not null) await BeforeConsentChangeAsync(); }
            finally { await _store.SuspendAsync(cancellationToken); }
        }
        finally { Changed?.Invoke(this, EventArgs.Empty); }
    }

    private static bool ConsentChanged(SharedPreferences left, SharedPreferences right)
        => left.Enabled != right.Enabled || left.AgreementVersion != right.AgreementVersion
            || left.EnabledAt != right.EnabledAt || left.Epoch != right.Epoch
            || left.PolicyRevision != right.PolicyRevision || left.ShareHistory != right.ShareHistory
            || left.Installation != right.Installation
            || !(left.ExcludedPaths ?? []).SequenceEqual(right.ExcludedPaths ?? [], StringComparer.OrdinalIgnoreCase);

    public string? LastError => _store.LastError;
}
