using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DiskLens.App.Services;

public sealed class AppSecretStore
{
    private readonly string _apiKeyPath;

    public AppSecretStore()
    {
        var settingsDirectory = AppDataDirectory.GetPath();
        _apiKeyPath = Path.Combine(settingsDirectory, "api-key.bin");
    }

    public async Task<string> LoadApiKeyAsync()
    {
        if (!File.Exists(_apiKeyPath))
        {
            return string.Empty;
        }

        try
        {
            var protectedBytes = await File.ReadAllBytesAsync(_apiKeyPath).ConfigureAwait(false);
            var plainBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return string.Empty;
        }
    }

    public async Task SaveApiKeyAsync(string apiKey)
    {
        var directory = Path.GetDirectoryName(_apiKeyPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (File.Exists(_apiKeyPath))
            {
                File.Delete(_apiKeyPath);
            }

            return;
        }

        var plainBytes = Encoding.UTF8.GetBytes(apiKey);
        var protectedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
        await File.WriteAllBytesAsync(_apiKeyPath, protectedBytes).ConfigureAwait(false);
    }
}
