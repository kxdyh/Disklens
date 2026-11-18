namespace DiskLens.Core.Policies;

public sealed record DeletionDecision(
    bool CanOpenLocation,
    bool CanRecycle,
    string Reason);
