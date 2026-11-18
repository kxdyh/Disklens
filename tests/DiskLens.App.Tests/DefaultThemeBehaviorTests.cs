using System.IO;
using System.Reflection;
using System.Text.Json;
using DiskLens.App.Models;
using DiskLens.App.Services;
using DiskLens.App.ViewModels;

namespace DiskLens.App.Tests;

[Collection("Isolated AI settings")]
public sealed class DefaultThemeBehaviorTests
{
    [Fact]
    public void Model_default_and_omitted_constructor_theme_use_the_rightmost_day_mode()
    {
        var omitted = new AppSettings("C:\\", 512, 512, DiskLens.Core.Models.AiProviderKind.Disabled, "", "");
        Assert.Equal("03", ThemeCatalog.DefaultThemeId);
        Assert.Equal(ThemeCatalog.DefaultThemeId, ThemeCatalog.Options.Last().Id);
        Assert.Equal("日间模式", ThemeCatalog.Options.Last().Name);
        Assert.Equal("03", omitted.ThemeId);
        Assert.Equal("03", AppSettings.Default().ThemeId);
        Assert.Equal("Resources/Themes/Theme03.xaml", ThemeCatalog.GetDictionaryPath(null));
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("omitted-theme")]
    [InlineData("corrupt")]
    [InlineData("null-document")]
    public async Task First_start_and_legacy_or_unreadable_settings_fall_back_to_day_mode(string scenario)
    {
        using var data = new ThemeData();
        if (scenario == "omitted-theme")
        {
            var legacy = JsonSerializer.SerializeToNode(AppSettings.Default() with { ThemeId = "01" })!.AsObject();
            legacy.Remove(nameof(AppSettings.ThemeId));
            await File.WriteAllTextAsync(data.SettingsPath, legacy.ToJsonString());
        }
        else if (scenario != "absent")
            await File.WriteAllTextAsync(data.SettingsPath, scenario == "corrupt" ? "{" : "null");

        Assert.Equal("03", (await new AppSettingsStore().LoadAsync()).ThemeId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("unsupported")]
    [InlineData("04")]
    public async Task Invalid_saved_theme_is_normalized_on_load_and_save(string? theme)
    {
        using var data = new ThemeData();
        var invalid = AppSettings.Default() with { ThemeId = theme! };
        // Exercise an old file directly, independent of the current save normalization.
        await File.WriteAllTextAsync(data.SettingsPath, JsonSerializer.Serialize(invalid));
        var store = new AppSettingsStore();
        Assert.Equal("03", (await store.LoadAsync()).ThemeId);

        await store.SaveAsync(invalid);
        using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(data.SettingsPath));
        Assert.Equal("03", saved.RootElement.GetProperty(nameof(AppSettings.ThemeId)).GetString());
        Assert.Equal("03", (await store.LoadAsync()).ThemeId);
    }

    [Theory]
    [InlineData("01")]
    [InlineData("02")]
    [InlineData("03")]
    public async Task Existing_valid_user_theme_survives_loading_and_future_saves(string theme)
    {
        using var data = new ThemeData();
        var chosen = AppSettings.Default() with { ThemeId = theme };
        await File.WriteAllTextAsync(data.SettingsPath, JsonSerializer.Serialize(chosen));
        var store = new AppSettingsStore();
        var loaded = await store.LoadAsync();
        Assert.Equal(theme, loaded.ThemeId);
        await store.SaveAsync(loaded with { AnalysisThresholdMb = 256 });
        Assert.Equal(theme, (await store.LoadAsync()).ThemeId);
    }

    private sealed class ThemeData : IDisposable
    {
        private readonly string? _previousData = Environment.GetEnvironmentVariable("DISKLENS_DATA_DIRECTORY");
        public string DirectoryPath { get; } = Path.Combine(
            Environment.GetEnvironmentVariable("DISKLENS_TEST_ARTIFACTS") ?? Path.GetTempPath(),
            "theme-settings-" + Guid.NewGuid().ToString("N"));
        public string SettingsPath => Path.Combine(DirectoryPath, "settings.json");

        public ThemeData()
        {
            Directory.CreateDirectory(DirectoryPath);
            Environment.SetEnvironmentVariable("DISKLENS_DATA_DIRECTORY", DirectoryPath);
        }

        public void Dispose() => Environment.SetEnvironmentVariable("DISKLENS_DATA_DIRECTORY", _previousData);
    }
}

public sealed partial class WindowLayoutRegressionTests
{
    [Fact]
    public void Default_theme_selects_the_rightmost_day_button_in_the_real_window() => OnSta(() =>
    {
        using var scene = new Scene(AppSettings.Default());
        var root = Layout(scene.Window, 1024, 640);
        var vm = scene.Workspace.ActiveViewModel;
        Assert.Equal("03", vm.SelectedThemeId);
        Assert.True(vm.IsDayThemeSelected);
        Assert.False(vm.IsNightThemeSelected);
        Assert.False(vm.IsWarmThemeSelected);
        var choices = Descendants<System.Windows.Controls.RadioButton>(root)
            .Where(button => button.GroupName == "WorkbenchTheme").ToArray();
        Assert.Equal(["01", "02", "03"], choices.Select(button => button.CommandParameter?.ToString()));
        Assert.True(choices.Last().IsChecked);
        Assert.Contains(scene.Window.Resources.MergedDictionaries,
            dictionary => dictionary.Source?.OriginalString.EndsWith("Theme03.xaml", StringComparison.Ordinal) == true);
    });

    [Theory]
    [InlineData(null)]
    [InlineData("invalid")]
    public void Default_theme_normalizes_shared_workspace_settings_and_restores_day_defaults(string? invalid) => OnSta(() =>
    {
        using var scene = new Scene(AppSettings.Default() with { ThemeId = "01" });
        var vm = scene.Workspace.ActiveViewModel;
        var apply = typeof(MainWindowViewModel).GetMethod("ApplySharedSettings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        apply.Invoke(vm, [AppSettings.Default() with { ThemeId = invalid! }, ""]);
        Assert.Equal("03", vm.SelectedThemeId);
        Assert.True(vm.IsDayThemeSelected);

        apply.Invoke(vm, [AppSettings.Default() with { ThemeId = "02" }, ""]);
        Assert.Equal("02", vm.SelectedThemeId);
        Assert.True(vm.IsWarmThemeSelected);
        apply.Invoke(vm, [AppSettings.Default(), ""]);
        Assert.Equal("03", vm.SelectedThemeId);
        Assert.True(vm.IsDayThemeSelected);
    });
}
