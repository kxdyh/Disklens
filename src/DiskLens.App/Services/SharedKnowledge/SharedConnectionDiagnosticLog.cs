using System.IO;
using System.Text.Json;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.App.Services.SharedKnowledge;

/// <summary>Bounded local connection metadata. Never stores credentials or request content.</summary>
internal static class SharedConnectionDiagnosticLog
{
    private static readonly object Gate = new();
    private const long MaximumBytes = 128 * 1024;

    internal static void Record(string directory, SharedRequestDiagnostic value)
    {
        try
        {
            var row = JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                atUtc = DateTimeOffset.UtcNow,
                operation = value.Operation,
                phase = value.Phase,
                outcome = value.Outcome,
                httpStatus = value.HttpStatus,
                elapsedMs = Math.Round(value.Elapsed.TotalMilliseconds, 3),
                timeoutMs = value.Timeout.TotalMilliseconds
            });
            lock (Gate)
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "connection-diagnostics.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length >= MaximumBytes)
                    File.Move(path, path + ".previous", overwrite: true);
                File.AppendAllText(path, row + Environment.NewLine);
            }
        }
        catch
        {
            // A full disk or unavailable log must never break the actual request.
        }
    }
}
