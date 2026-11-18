using DiskLens.App.Services.SharedKnowledge;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private int _sharedRecoveryAttempts;
    private long _sharedRecoveryRevision;

    private void ResetSharedLookupRecovery()
    {
        _sharedRecoveryAttempts = 0;
        ++_sharedRecoveryRevision;
    }

    private void SharedRuntime_OnConnectionChanged(object? sender, EventArgs e)
    {
        if (sender is not SharedKnowledgeRuntime runtime) return;
        PostSharedRecovery(() =>
        {
            if (_isDisposed || !ReferenceEquals(runtime, SharedRuntime)
                || _sharedConsent is not { CanShare: true }) return;
            IsOfflineAlertVisible = false;
            // An address notification is not evidence that the active read failed.
            // The transport selects the current route for subsequent requests;
            // bounded request failures drive recovery without restarting healthy work.
            if (_sharedLookupCancellation is not null || IsLoadingSharedExperience || _isPreparingSharedLookup)
            {
                RecordSharedLookupEvent(runtime, "NetworkChange", "KeptActiveRead");
                return;
            }
            // Reconnecting transport must not invalidate already loaded answers.
            if (!_sharedLookupIncomplete)
            {
                RecordSharedLookupEvent(runtime, "NetworkChange", "KeptCompletedResults");
                return;
            }
            ResetSharedLookupRecovery();
            // Old read-only work can be canceled. The shared upload queue has
            // its own acknowledged IDs and is never replayed here.
            CancelSharedLookup();
            _sharedLookupIncomplete = true;
            NotifySharedUploadFooter();
            RecordSharedLookupEvent(runtime, "NetworkChange", "RecoverInterruptedRead");
            ScheduleSharedLookupRecovery(networkChanged: true);
        });
    }

    private void ScheduleSharedLookupRecovery(bool networkChanged = false)
    {
        if (_isDisposed || !_sharedLookupIncomplete || SharedRuntime is not { } runtime || ScanRoot is not { } snapshot
            || _sharedConsent is not { CanShare: true } consent || _sharedRecoveryAttempts >= 3) return;
        var attempt = ++_sharedRecoveryAttempts;
        var revision = ++_sharedRecoveryRevision;
        var scanSession = _scanCancellationTokenSource;
        var rootPath = snapshot.Path;
        var delay = networkChanged ? 1 : attempt switch { 1 => 3, 2 => 8, _ => 20 };
        RecordSharedLookupEvent(runtime, "RecoveryScheduled", networkChanged ? "NetworkChange" : "IncompleteRead");
        _ = RecoverAsync();

        async Task RecoverAsync()
        {
            await Task.Delay(TimeSpan.FromSeconds(delay)).ConfigureAwait(false);
            PostSharedRecovery(() =>
            {
                if (_isDisposed || !_sharedLookupIncomplete || revision != _sharedRecoveryRevision
                    || !ReferenceEquals(runtime, SharedRuntime)
                    || !ReferenceEquals(scanSession, _scanCancellationTokenSource)
                    || !string.Equals(rootPath, ScanRoot?.Path, StringComparison.OrdinalIgnoreCase)
                    || !SameSharedConsent(consent, _sharedConsent)) return;
                ConsumePendingSharedConsentLookup();
                _sharedConsentLookupPending = true;
                // Existing idle-state coordination handles an ongoing scan or AI
                // request without starting a second lookup or invoking a model.
                TryQueuePendingSharedConsentLookup();
            });
        }
    }

    private static void RecordSharedLookupEvent(SharedKnowledgeRuntime runtime, string phase, string outcome)
        => SharedConnectionDiagnosticLog.Record(runtime.Store.StorageDirectory,
            new SharedRequestDiagnostic("Lookup", phase, outcome, null, TimeSpan.Zero, TimeSpan.Zero));

    private void PostSharedRecovery(Action apply)
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
