using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using DiskLens.App.Localization;
using DiskLens.App.Models;
using DiskLens.App.Services;
using DiskLens.Core.Models;

namespace DiskLens.App.Tests;

[Collection("Isolated AI settings")]
public sealed class InternationalizationBehaviorTests
{
    [Fact]
    public void Closed_windows_and_terminated_dispatchers_do_not_receive_language_refreshes()
    {
        Window? stoppedWindow = null;
        var stoppedRefreshes = 0;
        OnSta(() =>
        {
            stoppedWindow = new Window();
            WindowLanguage.Register(stoppedWindow, () => stoppedRefreshes++);
        });
        OnSta(() =>
        {
            var closedRefreshes = 0;
            var window = new Window();
            WindowLanguage.Register(window, () => closedRefreshes++);
            window.Close();
            L.SetLanguage("en");
            try
            {
                Assert.Equal(0, closedRefreshes);
                Assert.Equal(0, stoppedRefreshes);
                GC.KeepAlive(stoppedWindow);
            }
            finally { L.SetLanguage("zh"); }
        });
    }

    [Fact]
    public void English_templates_keep_paths_and_filenames_unchanged_and_translate_formatted_core_messages() => OnSta(() =>
    {
        var path = @"E:\文件\名称\扫描完成";
        L.SetLanguage("en");
        try
        {
            Assert.Equal("Settings", L.T("设置"));
            Assert.Equal("en-US", System.Globalization.CultureInfo.CurrentCulture.Name);
            Assert.Equal("November", new DateTime(2026, 11, 10).ToString("MMMM"));
            Assert.Equal("1,234.50", 1234.5.ToString("N2"));
            Assert.Equal("Opened folder: " + path, L.F($"已进入：{path}"));
            Assert.Equal("Found 12 items. Checking local history and shared experience.", L.T("已找到 12 项，正在检查本地历史与云端经验。"));
            Assert.Equal(path, L.T(path));
            Assert.Equal("Light mode", ThemeCatalog.Options.Last().Name);
            L.SetLanguage("zh");
            Assert.Equal("设置", L.T("设置"));
            Assert.Equal("日间模式", ThemeCatalog.Options.Last().Name);
        }
        finally { L.SetLanguage("zh"); }
    });

    [Fact]
    public void Static_and_dynamic_bindings_follow_language_changes_without_touching_user_bound_names() => OnSta(() =>
    {
        L.SetLanguage("zh");
        var root = new StackPanel();
        var label = new TextBlock();
        label.SetBinding(TextBlock.TextProperty, new Binding("[s592]") { Source = L.Current });
        var filename = new TextBlock();
        filename.SetBinding(TextBlock.TextProperty, new Binding(nameof(NameHolder.Name)) { Source = new NameHolder("设置") });
        root.Children.Add(label);
        root.Children.Add(filename);
        var window = new Window { Content = root };
        WindowLanguage.Attach(window);
        try
        {
            Assert.Equal("设置", label.Text);
            L.SetLanguage("en");
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
            Assert.Equal("Settings", label.Text);
            Assert.Equal("设置", filename.Text);
            L.SetLanguage("zh");
            Assert.Equal("设置", label.Text);
        }
        finally { L.SetLanguage("zh"); window.Close(); }
    });

    [Fact]
    public async Task Older_settings_default_to_Chinese_and_new_preferences_round_trip()
    {
        var old = Environment.GetEnvironmentVariable("DISKLENS_DATA_DIRECTORY");
        var directory = Path.Combine(Environment.GetEnvironmentVariable("DISKLENS_TEST_ARTIFACTS") ?? Path.GetTempPath(), "i18n-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("DISKLENS_DATA_DIRECTORY", directory);
        try
        {
            var json = JsonSerializer.SerializeToNode(AppSettings.Default())!.AsObject();
            json.Remove(nameof(AppSettings.Language));
            File.WriteAllText(Path.Combine(directory, "settings.json"), json.ToJsonString());
            var store = new AppSettingsStore();
            Assert.Equal("zh", (await store.LoadAsync()).Language);
            await store.SaveAsync(AppSettings.Default() with { Language = "en-US" });
            Assert.Equal("en", (await store.LoadAsync()).Language);
        }
        finally { Environment.SetEnvironmentVariable("DISKLENS_DATA_DIRECTORY", old); }
    }

    [Fact]
    public void Analysis_selects_matching_language_and_distinguishes_legacy_text_from_real_failure() => OnSta(() =>
    {
        var result = new AiAnalysisResult(@"C:\资料\数据", RiskLevel.Low, SuggestedAction.RecycleCandidate, "应用缓存和临时数据", .9)
        {
            ShortPurpose = "应用缓存",
            ShortPurposeEn = "Application cache",
            ReasonEn = "Cached application data that can be regenerated.",
            ConfidenceLevel = AiConfidenceLevel.High,
        };
        L.SetLanguage("en");
        try
        {
            Assert.Equal("Application cache", L.Purpose(result));
            Assert.Equal(result.ReasonEn, L.Reason(result));
            Assert.Contains("older result", L.Reason(result with { ReasonEn = "" }));
            var failed = AiAnalysisResult.CreateFallback(result.Path, "分析已取消，已完成结果已保留。");
            Assert.Equal("Analysis was canceled. Completed results have been kept.", L.Reason(failed));
            L.SetLanguage("zh");
            Assert.Equal("应用缓存", L.Purpose(result));
            Assert.Equal("应用缓存和临时数据", L.Reason(result));
            Assert.Equal(@"C:\资料\数据", result.Path);
        }
        finally { L.SetLanguage("zh"); }
    });

    [Fact]
    public void Embedded_translation_catalog_has_a_nonempty_English_value_for_every_static_key()
    {
        using var keys = typeof(L).Assembly.GetManifestResourceStream("DiskLens.App.Localization.keys.json")!;
        using var english = typeof(L).Assembly.GetManifestResourceStream("DiskLens.App.Localization.en.json")!;
        var sources = JsonSerializer.Deserialize<Dictionary<string, string>>(keys)!;
        var values = JsonSerializer.Deserialize<Dictionary<string, string>>(english)!;
        Assert.True(sources.Count > 670);
        foreach (var source in sources.Values) Assert.False(string.IsNullOrWhiteSpace(values.GetValueOrDefault(source)), source);
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { L.SetLanguage("zh"); action(); }
            catch (Exception e) { failure = e; }
            finally { L.SetLanguage("zh"); Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Internationalization dispatcher test timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private sealed record NameHolder(string Name);
}
