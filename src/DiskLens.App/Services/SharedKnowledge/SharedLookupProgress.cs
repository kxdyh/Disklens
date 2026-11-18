namespace DiskLens.App.Services.SharedKnowledge;

public enum SharedLookupStage { Preparing, DownloadingIndex, Matching, Retrying, Completed }

/// <summary>Real local path counts; Total is zero while the complete index/candidate set is still unknown.</summary>
public sealed record SharedLookupProgress(SharedLookupStage Stage, int Processed, int Total, int Matched, int Failed);
