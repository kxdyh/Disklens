using System.IO;
using System.Text.Json;
using DiskLens.App.Models;
using DiskLens.Core.Models;

namespace DiskLens.App.Services;

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _settingsPath;

    public AppSettingsStore()
    {
        var settingsDirectory = AppDataDirectory.GetPath();
        _settingsPath = Path.Combine(settingsDirectory, "settings.json");
    }

    public async Task<AppSettings> LoadAsync()
    {
        if (!File.Exists(_settingsPath))
        {
            return AppSettings.Default();
        }

        try
        {
            await using var stream = File.OpenRead(_settingsPath);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, SerializerOptions).ConfigureAwait(false)
                ?? AppSettings.Default();
            settings = NormalizeLegacyAiDefaults(settings);
            return settings with
            {
                AiConnectionProfileId = AiConnectionOption.ResolveProfile(settings),
                MaxAiBatchSize = AppSettings.NormalizeAiBatchSize(settings.MaxAiBatchSize),
                ThemeId = ThemeCatalog.Normalize(settings.ThemeId),
                Language = L.Normalize(settings.Language),
            };
        }
        catch
        {
            return AppSettings.Default();
        }
    }

    public async Task SaveAsync(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(_settingsPath);
        var normalized = settings with
        {
            MaxAiBatchSize = AppSettings.NormalizeAiBatchSize(settings.MaxAiBatchSize),
            ThemeId = ThemeCatalog.Normalize(settings.ThemeId),
                Language = L.Normalize(settings.Language),
        };
        await JsonSerializer.SerializeAsync(stream, normalized, SerializerOptions).ConfigureAwait(false);
    }

    private static AppSettings NormalizeLegacyAiDefaults(AppSettings settings)
    {
        if (settings.ProviderKind != AiProviderKind.Disabled)
        {
            return settings;
        }

        var usesLegacyEndpoint = string.Equals(settings.AiEndpoint, "http://localhost:11434", StringComparison.OrdinalIgnoreCase);
        var usesLegacyModel = string.Equals(settings.AiModel, "qwen2.5:7b", StringComparison.OrdinalIgnoreCase);
        if (!usesLegacyEndpoint && !usesLegacyModel)
        {
            return settings;
        }

        return settings with
        {
            AiEndpoint = string.Empty,
            AiModel = string.Empty,
        };
    }
}
