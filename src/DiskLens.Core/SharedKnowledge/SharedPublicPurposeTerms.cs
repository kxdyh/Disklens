namespace DiskLens.Core.SharedKnowledge;

/// <summary>Exact reviewed public product and standard folder names. Never populated from local paths or registry metadata.</summary>
internal static class SharedPublicPurposeTerms
{
    private static readonly HashSet<string> PathNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Adobe", "app", "Application", "assembly", "claude",
        "Codex", "Docker", "Edge", "Fonts", "Installer",
        "Java", "JetBrains", "kingsoft", "NVIDIA", "OpenAI",
        "System32", "Tencent", "WeGame", "Windows Defender", "wps",
        "wsl", "NuGet", "Gradle", "Cargo", "Rustup", "npm", "Maven", "P2", "virtualenvs", "InsightFace", "Android", "Cursor",
    };
    private static readonly HashSet<string> EnglishPurposeWords = new(
        ("application applications temporary environment component components configuration library libraries installation "
        + "runtime service services local update updates installer maintain software system files shared storage media "
        + "processing model models dependencies development developer resource resources data online offline client "
        + "browser browsers package packages training inference stable diffusion program programs architecture operating "
        + "windows python version versions management manager project projects language languages integration workflow "
        + "workflows chat chatbot generative assistant assistants analysis analyze tool tools binary binaries editor editing "
        + "texture textures graphics graphic render rendering engine engines image images video videos audio code codec "
        + "codecs driver drivers machine learning network networking internet communication collaboration messaging game "
        + "games gaming launcher launchers transcription interface interfaces support bridge connector connectors plugin "
        + "plugins input output log logs build compile compiling scripting script scripts download downloads content "
        + "contents platform platforms profile profiles extension extensions").Split(' '), StringComparer.OrdinalIgnoreCase);

    static SharedPublicPurposeTerms()
    {
        // These entries come only from the fixed reviewed catalog, never from a user's filesystem.
        PathNames.UnionWith(SharedStandardPurposeCatalog.WindowsDirectories.Select(directory => directory.Name));
        PathNames.UnionWith(SharedStandardPurposeCatalog.ProfileDirectories.Select(directory => directory.Name));
    }

    internal static bool IsPublicPathName(string value) => PathNames.Contains(value);
    internal static bool IsPublicEnglishPurposeWord(string value) => EnglishPurposeWords.Contains(value);
}
