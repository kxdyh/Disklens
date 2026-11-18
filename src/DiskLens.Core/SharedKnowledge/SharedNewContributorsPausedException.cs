namespace DiskLens.Core.SharedKnowledge;

/// <summary>A fixed service state; no remote response text is retained.</summary>
public sealed class SharedNewContributorsPausedException() : Exception("New contributors are temporarily paused.") { }
