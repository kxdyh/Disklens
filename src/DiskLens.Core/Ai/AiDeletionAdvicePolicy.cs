using DiskLens.Core.Models;

namespace DiskLens.Core.Ai;

/// <summary>Rejects contradictory removal advice without inventing a positive recommendation.</summary>
public static class AiDeletionAdvicePolicy
{
    public static AiAnalysisResult Constrain(AiAnalysisResult result, bool protectedLocation = false)
    {
        if (result.SuggestedAction != SuggestedAction.RecycleCandidate) return result;
        var category = AiPurposeCategory.Normalize(result.PurposeCategory);
        if (!protectedLocation && result.RiskLevel is not (RiskLevel.High or RiskLevel.Protected)
            && category is not ("runtime" or "application" or "configuration" or "user-data")
            && category != AiPurposeCategory.Unknown
            && result.EffectiveConfidenceLevel is AiConfidenceLevel.Medium or AiConfidenceLevel.High
            && !IsMixedPersonalRoot(result.Path) && !IsPersistentPersonalData(result.Path)) return result;
        return result with
        {
            SuggestedAction = SuggestedAction.DoNotDelete,
            RiskLevel = protectedLocation || result.RiskLevel == RiskLevel.Protected
                ? RiskLevel.Protected : RiskLevel.High,
        };
    }

    private static bool IsMixedPersonalRoot(string path)
    {
        var parts = path.Replace('/', '\\').TrimEnd('\\').Split('\\');
        if (parts.Length == 1 && parts[0].EndsWith(':')) return true;
        if (parts.Length < 2 || !parts[1].Equals("Users", StringComparison.OrdinalIgnoreCase)) return false;
        if (parts.Length <= 3) return true;
        if (parts.Length == 4 && new[] { "Downloads", "Documents", "Desktop", "Pictures", "Videos", "Music", "AppData" }
            .Contains(parts[3], StringComparer.OrdinalIgnoreCase)) return true;
        return parts.Length == 5 && parts[3].Equals("AppData", StringComparison.OrdinalIgnoreCase)
            && new[] { "Local", "LocalLow", "Roaming" }.Contains(parts[4], StringComparer.OrdinalIgnoreCase);
    }

    public static void ConstrainAncestors(IDictionary<string, AiAnalysisResult> results)
    {
        var mixedParents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var result in results.Values)
        {
            if (result.LooksLikeFallback() || result.SuggestedAction == SuggestedAction.RecycleCandidate) continue;
            if (result.RiskLevel is not (RiskLevel.High or RiskLevel.Protected)
                && result.PurposeCategory is not ("runtime" or "application" or "configuration" or "user-data")) continue;
            var parent = result.Path.Replace('/', '\\').TrimEnd('\\');
            while (parent.LastIndexOf('\\') is var separator && separator > 1)
            {
                parent = parent[..separator];
                mixedParents.Add(parent);
            }
        }
        foreach (var pair in results.ToArray())
            if (pair.Value.SuggestedAction == SuggestedAction.RecycleCandidate
                && mixedParents.Contains(pair.Key.Replace('/', '\\').TrimEnd('\\')))
                results[pair.Key] = pair.Value with { SuggestedAction = SuggestedAction.DoNotDelete, RiskLevel = RiskLevel.High };
    }

    private static bool IsPersistentPersonalData(string path)
    {
        var normalized = path.Replace('/', '\\').TrimEnd('\\');
        var parts = normalized.Split('\\');
        // Installed game assets are not disposable just because their suffix
        // says "cache". Explicit runtime-generated ShaderCache is separate.
        var steam = Array.FindIndex(parts, part => part.Equals("steamapps", StringComparison.OrdinalIgnoreCase));
        if (steam >= 0 && steam + 1 < parts.Length
            && (parts[steam + 1].Equals("workshop", StringComparison.OrdinalIgnoreCase)
                || parts[steam + 1].Equals("common", StringComparison.OrdinalIgnoreCase)
                    && !parts.Skip(steam + 3).Any(part => part.Equals("ShaderCache", StringComparison.OrdinalIgnoreCase)))) return true;
        if (parts.Any(part => part.Equals("runtimes", StringComparison.OrdinalIgnoreCase)
            || part.EndsWith("-runtimes", StringComparison.OrdinalIgnoreCase))) return true;
        var users = Array.FindIndex(parts, part => part.Equals("Users", StringComparison.OrdinalIgnoreCase));
        if (users >= 0 && (parts.Length <= users + 2 || parts.Length == users + 3
            && parts[^1].Equals("Downloads", StringComparison.OrdinalIgnoreCase))) return true;
        // Downloads can contain disposable installers; personal documents and
        // active stores are different even if a model calls them a cache.
        if (parts.Any(part => new[] { "IndexedDB", "Local Storage", "Session Storage", "Service Worker",
                "Saved Games", "saves", "models", "weights", "checkpoints" }
            .Contains(part, StringComparer.OrdinalIgnoreCase))) return true;
        if (parts.Length >= 4 && parts[1].Equals("Users", StringComparison.OrdinalIgnoreCase)
            && new[] { "Documents", "Desktop", "Pictures", "Videos", "Music" }
                .Contains(parts[3], StringComparer.OrdinalIgnoreCase)) return true;
        foreach (var folder in new[] { Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.DesktopDirectory,
                     Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.MyMusic })
        {
            var root = Environment.GetFolderPath(folder).TrimEnd('\\');
            if (root.Length > 0 && (normalized.Equals(root, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))) return true;
        }
        var name = parts[^1];
        return new[] { ".db", ".sqlite", ".sqlite3", ".vhd", ".vhdx", ".qcow2", ".gguf", ".safetensors" }
            .Any(extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            || name.EndsWith("-wal", StringComparison.OrdinalIgnoreCase) || name.EndsWith("-shm", StringComparison.OrdinalIgnoreCase);
    }
}
