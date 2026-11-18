namespace DiskLens.App.Models;

public sealed record ThemeOption(string Id, string Name, string Description);

public static class ThemeCatalog
{
    public const string DefaultThemeId = "03";

    public static IReadOnlyList<ThemeOption> Options =>
    [
        new("01", L.T("夜间模式"), L.T("深色背景，搭配柔和绿色强调色")),
        new("02", L.T("暖色模式"), L.T("暖色背景，搭配自然绿色强调色")),
        new("03", L.T("日间模式"), L.T("浅色背景，搭配清爽绿色强调色")),
    ];

    public static string Normalize(string? themeId)
    {
        return Options.Any(option => string.Equals(option.Id, themeId, StringComparison.Ordinal))
            ? themeId!
            : DefaultThemeId;
    }

    public static string GetDictionaryPath(string? themeId)
    {
        return $"Resources/Themes/Theme{Normalize(themeId)}.xaml";
    }
}
