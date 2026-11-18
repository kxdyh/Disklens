namespace DiskLens.App.Models;

/// <summary>
/// A task-owned reveal clock. Only the treemap that is actually showing this
/// task advances it, so changing tabs never consumes a pending reveal.
/// </summary>
public sealed class TreemapPurposeRevealBatch
{
    private const double DurationMilliseconds = 320;
    private const double StaggerMilliseconds = 125;
    private readonly Dictionary<string, Reveal> _reveals = new(StringComparer.OrdinalIgnoreCase);
    private double _elapsedMilliseconds;
    private double? _lastTickMilliseconds;

    public bool HasRunningReveals => _reveals.Values.Any(reveal =>
        reveal.StartMilliseconds is { } start && _elapsedMilliseconds < start + DurationMilliseconds);

    public void Enqueue(string path, string purpose)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(purpose)) return;
        purpose = purpose.Trim();
        if (_reveals.TryGetValue(path, out var existing) && existing.Purpose == purpose) return;
        _reveals[path] = new Reveal(purpose);
    }

    public void AdvanceTo(double nowMilliseconds)
    {
        if (_lastTickMilliseconds is { } previous)
            _elapsedMilliseconds += Math.Max(0, nowMilliseconds - previous);
        _lastTickMilliseconds = nowMilliseconds;
    }

    public void Pause() => _lastTickMilliseconds = null;

    public void RevealVisible(IEnumerable<KeyValuePair<string, string>> visiblePurposes)
    {
        var nextStart = Math.Max(_elapsedMilliseconds, _reveals.Values
            .Where(reveal => reveal.StartMilliseconds.HasValue)
            .Select(reveal => reveal.StartMilliseconds!.Value + StaggerMilliseconds)
            .DefaultIfEmpty(_elapsedMilliseconds).Max());
        foreach (var (path, purpose) in visiblePurposes)
        {
            if (!_reveals.TryGetValue(path, out var reveal) || reveal.Purpose != purpose
                || reveal.StartMilliseconds.HasValue) continue;
            reveal.StartMilliseconds = nextStart;
            nextStart += StaggerMilliseconds;
        }
    }

    public double GetProgress(string path, string purpose)
    {
        if (!_reveals.TryGetValue(path, out var reveal) || reveal.Purpose != purpose) return 1;
        if (reveal.StartMilliseconds is not { } start) return 0;
        var progress = Math.Clamp((_elapsedMilliseconds - start) / DurationMilliseconds, 0, 1);
        return 1 - Math.Pow(1 - progress, 3);
    }

    public void CompleteAll()
    {
        foreach (var reveal in _reveals.Values)
            reveal.StartMilliseconds = _elapsedMilliseconds - DurationMilliseconds;
        Pause();
    }

    public void Clear()
    {
        _reveals.Clear();
        _elapsedMilliseconds = 0;
        Pause();
    }

    private sealed class Reveal(string purpose)
    {
        public string Purpose { get; } = purpose;
        public double? StartMilliseconds { get; set; }
    }
}
