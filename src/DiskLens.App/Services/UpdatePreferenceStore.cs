using System.IO;
using System.Text.Json;

namespace DiskLens.App.Services;

public interface IUpdatePreferenceStore
{
    Task<string?> LoadIgnoredVersionAsync(CancellationToken cancellationToken);
    Task SaveIgnoredVersionAsync(string version, CancellationToken cancellationToken);
}

public sealed class UpdatePreferenceStore(string directory) : IUpdatePreferenceStore
{
    private readonly string _path = Path.Combine(directory, "update-preferences.json");
    private readonly SemaphoreSlim _gate = new(1, 1);
    public UpdatePreferenceStore() : this(AppDataDirectory.GetPath()) { }

    public async Task<string?> LoadIgnoredVersionAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return null;
            var content = await File.ReadAllBytesAsync(_path, cancellationToken).ConfigureAwait(false);
            if (content.Length > 2048) return null;
            return JsonSerializer.Deserialize<UpdatePreference>(content)?.IgnoredVersion;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
        finally { _gate.Release(); }
    }

    public async Task SaveIgnoredVersionAsync(string version, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporary = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(new UpdatePreference(version)), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            finally { _gate.Release(); }
        }
    }

    private sealed record UpdatePreference(string IgnoredVersion);
}
