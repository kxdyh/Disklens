namespace DiskLens.App.Services.SharedKnowledge;

public enum SharedUploadStage { Idle, Pending, Uploading, Completed, Failed, Paused }

/// <summary>A service-wide display snapshot. Counts describe validated server receipts, never sent requests.</summary>
public sealed record SharedUploadStatus(SharedUploadStage Stage, int Pending, int Accepted = 0,
    int Skipped = 0, int Total = 0, string? Error = null)
{
    public int Confirmed => Accepted + Skipped;
}
