using DiskLens.App.Services.SharedKnowledge;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private void SharedRuntime_OnConnectionChanged(object? sender, EventArgs e)
    {
        if (sender is not SharedKnowledgeRuntime runtime) return;
        PostSharedConnectionChange(() =>
        {
            if (_isDisposed || !ReferenceEquals(runtime, SharedRuntime)
                || _sharedConsent is not { CanShare: true }) return;
            IsOfflineAlertVisible = false;
            // The transport can reconnect; matching still needs a user action after an incomplete pass.
            RecordSharedLookupEvent(runtime, "NetworkChange", _sharedLookupIncomplete ? "AwaitingManualRetry"
                : _isPreparingSharedLookup || IsLoadingSharedExperience ? "KeptActiveRead" : "KeptCompletedResults");
        });
    }

    private static void RecordSharedLookupEvent(SharedKnowledgeRuntime runtime, string phase, string outcome)
        => SharedConnectionDiagnosticLog.Record(runtime.Store.StorageDirectory,
            new SharedRequestDiagnostic("Lookup", phase, outcome, null, TimeSpan.Zero, TimeSpan.Zero));

    private void PostSharedConnectionChange(Action apply)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null)
        {
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
            if (dispatcher.CheckAccess()) apply();
            else dispatcher.BeginInvoke(apply);
        }
        else if (_sharedOutboxUiContext is { } context
            && !ReferenceEquals(SynchronizationContext.Current, context)) context.Post(_ => apply(), null);
        else apply();
    }
}
