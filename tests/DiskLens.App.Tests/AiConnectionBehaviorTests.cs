using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using DiskLens.App.Models;
using DiskLens.App.Services;
using DiskLens.App.ViewModels;
using DiskLens.Core.Ai;
using DiskLens.Core.Models;
using DiskLens.Core.Policies;
using DiskLens.Core.Scanning;

namespace DiskLens.App.Tests;

[CollectionDefinition("Isolated AI settings", DisableParallelization = true)]
public sealed class IsolatedAiSettingsCollection;

[Collection("Isolated AI settings")]
public sealed class AiConnectionBehaviorTests
{
    private const string SyntheticKey = "synthetic-test-key-never-sent";

    [Fact]
    public void Fresh_settings_offer_only_the_three_friendly_connections() => OnSta(() =>
    {
        using var data = new IsolatedData();
        var vm = CreateViewModel(AppSettings.Default());
        Assert.Equal(["暂不启用", "DeepSeek", "自定义 OpenAI 兼容接口"], vm.AiConnectionOptions.Select(option => option.Label));
        Assert.Equal(AiConnectionOption.DisabledId, vm.SelectedAiConnectionId);
        Assert.False(vm.IsAiConnectionEnabled);
        Assert.False(vm.HasAiConnectionConfiguration);
        Assert.True(vm.ShowAiConnectionSetupAction);
        Assert.DoesNotContain(vm.AiConnectionOptions, option => option.Id == AiConnectionOption.LegacyOllamaId);
    });

    [Fact]
    public void Bound_connection_selector_applies_DeepSeek_defaults_and_preserves_the_key() => OnSta(() =>
    {
        using var data = new IsolatedData();
        var vm = CreateViewModel(AppSettings.Default());
        SuspendDeferredSaves(vm);
        var selector = new ComboBox { DisplayMemberPath = "Label", SelectedValuePath = "Id" };
        selector.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(vm.AiConnectionOptions)) { Source = vm });
        selector.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedValueProperty,
            new Binding(nameof(vm.SelectedAiConnectionId)) { Source = vm, Mode = BindingMode.TwoWay });
        PumpDispatcher();
        Assert.Equal(AiConnectionOption.DisabledId, selector.SelectedValue);

        selector.SelectedValue = AiConnectionOption.DeepSeekId;
        PumpDispatcher();

        Assert.Equal(AiConnectionOption.DeepSeekId, vm.SelectedAiConnectionId);
        Assert.Equal(AiProviderKind.OpenAiCompatible, vm.SelectedProviderKind);
        Assert.Equal("https://api.deepseek.com", vm.AiEndpoint);
        Assert.Equal("deepseek-flash", vm.AiModel);
        Assert.Equal("medium", vm.AiReasoningEffort);
        Assert.True(vm.IsDeepSeekConnection);
        Assert.True(vm.HasAiConnectionConfiguration);
        Assert.True(vm.IsAiConnectionEnabled);
        Assert.True(vm.ApiKey == SyntheticKey);

        selector.SelectedValue = AiConnectionOption.CustomId;
        PumpDispatcher();
        Assert.True(vm.IsCustomAiConnection);
        Assert.False(vm.IsDeepSeekConnection);
        Assert.Equal("https://api.deepseek.com", vm.AiEndpoint);
        selector.SelectedValue = AiConnectionOption.DisabledId;
        PumpDispatcher();
        Assert.Equal(AiProviderKind.Disabled, vm.SelectedProviderKind);
        Assert.False(vm.IsAiConnectionEnabled);
        Assert.True(vm.ApiKey == SyntheticKey);
    });

    [Theory]
    [InlineData("https://api.deepseek.com")]
    [InlineData("https://api.deepseek.com/")]
    [InlineData("https://API.DEEPSEEK.COM/v1/")]
    public void Old_DeepSeek_settings_migrate_without_overwriting_user_configuration(string endpoint) => OnSta(() =>
    {
        using var data = new IsolatedData();
        var original = ConfiguredSettings(endpoint);
        var json = JsonSerializer.SerializeToNode(original)!.AsObject();
        json.Remove(nameof(AppSettings.AiConnectionProfileId));
        File.WriteAllText(Path.Combine(data.Path, "settings.json"), json.ToJsonString());
        var loaded = new AppSettingsStore().LoadAsync().GetAwaiter().GetResult();
        var vm = CreateViewModel(loaded);

        Assert.Equal(AiConnectionOption.DeepSeekId, loaded.AiConnectionProfileId);
        Assert.True(vm.IsDeepSeekConnection);
        Assert.Equal(endpoint, vm.AiEndpoint);
        Assert.Equal(original.AiModel, vm.AiModel);
        Assert.Equal(original.AiReasoningEffort, vm.AiReasoningEffort);
        Assert.True(vm.ApiKey == SyntheticKey);
    });

    [Theory]
    [InlineData("https://api.deepseek.com.evil.invalid")]
    [InlineData("https://example.invalid/deepseek")]
    public void Unrelated_endpoints_are_not_migrated_to_DeepSeek(string endpoint) => OnSta(() =>
    {
        using var data = new IsolatedData();
        var vm = CreateViewModel(ConfiguredSettings(endpoint));
        Assert.True(vm.IsCustomAiConnection);
        Assert.Equal(endpoint, vm.AiEndpoint);
    });

    [Fact]
    public void Explicit_custom_profile_using_official_endpoint_survives_save_and_restart() => OnSta(() =>
    {
        using var data = new IsolatedData();
        var vm = CreateViewModel(ConfiguredSettings(AiConnectionOption.DeepSeekEndpoint));
        SuspendDeferredSaves(vm);
        vm.SelectedAiConnectionId = AiConnectionOption.CustomId;
        SaveSettings(vm);

        var loaded = new AppSettingsStore().LoadAsync().GetAwaiter().GetResult();
        var reloadedKey = new AppSecretStore().LoadApiKeyAsync().GetAwaiter().GetResult();
        var restarted = CreateViewModel(loaded, reloadedKey);
        Assert.True(restarted.IsCustomAiConnection);
        Assert.Equal(AiConnectionOption.CustomId, loaded.AiConnectionProfileId);
        Assert.Equal(AiProviderKind.OpenAiCompatible, loaded.ProviderKind);
        Assert.Equal(AiConnectionOption.DeepSeekEndpoint, restarted.AiEndpoint);
        Assert.Equal("retained-user-model", restarted.AiModel);
        Assert.Equal("high", restarted.AiReasoningEffort);
        Assert.True(restarted.ApiKey == SyntheticKey);
        Assert.DoesNotContain(SyntheticKey, File.ReadAllText(Path.Combine(data.Path, "settings.json")));
    });

    [Fact]
    public void Selecting_DeepSeek_automatically_persists_a_complete_connection_and_disabling_keeps_the_key() => OnSta(() =>
    {
        using var data = new IsolatedData();
        var vm = CreateViewModel(AppSettings.Default());
        var store = new AppSettingsStore();
        var secrets = new AppSecretStore();
        vm.SelectedAiConnectionId = AiConnectionOption.DeepSeekId;
        WaitUntil(() =>
        {
            var saved = store.LoadAsync().GetAwaiter().GetResult();
            return saved.AiConnectionProfileId == AiConnectionOption.DeepSeekId
                && saved.ProviderKind == AiProviderKind.OpenAiCompatible
                && saved.AiEndpoint == AiConnectionOption.DeepSeekEndpoint
                && saved.AiModel == AiConnectionOption.DeepSeekModel
                && saved.AiReasoningEffort == AiConnectionOption.DeepSeekReasoningEffort
                && secrets.LoadApiKeyAsync().GetAwaiter().GetResult() == SyntheticKey;
        });

        vm.SelectedAiConnectionId = AiConnectionOption.DisabledId;
        WaitUntil(() => File.Exists(Path.Combine(data.Path, "settings.json"))
            && store.LoadAsync().GetAwaiter().GetResult().AiConnectionProfileId == AiConnectionOption.DisabledId);
        Assert.True(secrets.LoadApiKeyAsync().GetAwaiter().GetResult() == SyntheticKey);
        Assert.False(vm.HasSettingsSaveError);
        Assert.True(vm.HasAiConnectionConfiguration);
        Assert.False(vm.ShowAiConnectionSetupAction);
        Assert.True(CreateViewModel(store.LoadAsync().GetAwaiter().GetResult()).HasAiConnectionConfiguration);
    });

    [Fact]
    public void Existing_Ollama_configuration_keeps_its_compatibility_choice_and_survives_switching() => OnSta(() =>
    {
        using var data = new IsolatedData();
        var original = ConfiguredSettings("http://localhost:11434") with
        {
            ProviderKind = AiProviderKind.Ollama,
            AiModel = "existing-local-model",
        };
        var vm = CreateViewModel(original);
        SuspendDeferredSaves(vm);
        Assert.True(vm.IsLegacyOllamaConnection);
        Assert.True(vm.HasAiConnectionConfiguration);
        Assert.Contains(vm.AiConnectionOptions, option => option.Id == AiConnectionOption.LegacyOllamaId);
        Assert.Equal(original.AiEndpoint, vm.AiEndpoint);
        Assert.Equal(original.AiModel, vm.AiModel);
        vm.SelectedAiConnectionId = AiConnectionOption.DeepSeekId;
        vm.SelectedAiConnectionId = AiConnectionOption.LegacyOllamaId;
        SaveSettings(vm);

        var loaded = new AppSettingsStore().LoadAsync().GetAwaiter().GetResult();
        var restarted = CreateViewModel(loaded);
        Assert.True(restarted.IsLegacyOllamaConnection);
        Assert.Equal(AiProviderKind.Ollama, restarted.SelectedProviderKind);
        Assert.Equal(original.AiEndpoint, restarted.AiEndpoint);
        Assert.Equal(original.AiModel, restarted.AiModel);
        Assert.Equal(original.AiReasoningEffort, restarted.AiReasoningEffort);
        Assert.True(vm.ApiKey == SyntheticKey);
    });

    [Fact]
    public void Shared_Ollama_configuration_adds_the_compatibility_choice_to_newer_tasks_and_can_be_reselected() => OnSta(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        using var data = new IsolatedData();
        var original = ConfiguredSettings("http://localhost:11434") with
        {
            RootPath = data.Path,
            ProviderKind = AiProviderKind.Ollama,
            AiModel = "existing-local-model",
        };
        using var workspace = new ScanWorkspaceViewModel((settings, key) => CreateViewModel(settings, key),
            original, SyntheticKey, new AppSettingsStore(), new AppSecretStore(),
            (_, _, _) => Task.CompletedTask);
        workspace.HomeViewModel.SelectedAiConnectionId = AiConnectionOption.DeepSeekId;
        SaveSettings(workspace.HomeViewModel);
        workspace.StartScanAsync(data.Path).GetAwaiter().GetResult();
        var newer = workspace.ActiveViewModel;
        Assert.DoesNotContain(newer.AiConnectionOptions, option => option.Id == AiConnectionOption.LegacyOllamaId);
        var selector = new ComboBox { DisplayMemberPath = "Label", SelectedValuePath = "Id" };
        selector.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(newer.AiConnectionOptions)) { Source = newer });
        selector.SetBinding(System.Windows.Controls.Primitives.Selector.SelectedValueProperty,
            new Binding(nameof(newer.SelectedAiConnectionId)) { Source = newer, Mode = BindingMode.TwoWay });
        PumpDispatcher();

        workspace.HomeViewModel.SelectedAiConnectionId = AiConnectionOption.LegacyOllamaId;
        SaveSettings(workspace.HomeViewModel);
        PumpDispatcher();
        Assert.Equal(AiConnectionOption.LegacyOllamaId, selector.SelectedValue);
        Assert.All(workspace.TaskTabs, tab =>
            Assert.Contains(tab.ViewModel.AiConnectionOptions, option => option.Id == AiConnectionOption.LegacyOllamaId));

        selector.SelectedValue = AiConnectionOption.DeepSeekId;
        PumpDispatcher();
        SaveSettings(newer);
        Assert.True(newer.IsDeepSeekConnection);
        selector.SelectedValue = AiConnectionOption.LegacyOllamaId;
        PumpDispatcher();
        SaveSettings(newer);

        Assert.Equal(AiConnectionOption.LegacyOllamaId, selector.SelectedValue);
        Assert.All(workspace.TaskTabs, tab =>
        {
            var vm = tab.ViewModel;
            Assert.True(vm.IsLegacyOllamaConnection);
            Assert.Equal(AiProviderKind.Ollama, vm.SelectedProviderKind);
            Assert.Equal(original.AiEndpoint, vm.AiEndpoint);
            Assert.Equal(original.AiModel, vm.AiModel);
            Assert.Equal(original.AiReasoningEffort, vm.AiReasoningEffort);
            Assert.True(vm.ApiKey == SyntheticKey);
        });
        var saved = new AppSettingsStore().LoadAsync().GetAwaiter().GetResult();
        Assert.Equal(AiConnectionOption.LegacyOllamaId, saved.AiConnectionProfileId);
        Assert.Equal(original.AiEndpoint, saved.AiEndpoint);
        Assert.Equal(original.AiModel, saved.AiModel);
        Assert.Equal(original.AiReasoningEffort, saved.AiReasoningEffort);
        Assert.True(new AppSecretStore().LoadApiKeyAsync().GetAwaiter().GetResult() == SyntheticKey);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Onboarding_persists_the_same_connection_profile_as_settings(bool deepSeek) => OnSta(() =>
    {
        using var data = new IsolatedData();
        var vm = CreateViewModel(AppSettings.Default());
        var emptyRoot = new FileSystemNode(@"C:\", "C:", string.Empty, 0, FileAttributes.Directory,
            null, null, true, false, [], []);
        typeof(MainWindowViewModel).GetMethod("ApplySnapshot", BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(FileSystemNode), typeof(string), typeof(string)], null)!.Invoke(vm, [emptyRoot, null, null]);
        vm.OpenAiSetupCommand.Execute(null);
        if (deepSeek) vm.ChooseDeepSeekCommand.Execute(null);
        else vm.ChooseCustomAiCommand.Execute(null);
        vm.AiSetupEndpoint = AiConnectionOption.DeepSeekEndpoint;
        vm.AiSetupModel = "retained-user-model";
        vm.AiSetupReasoningEffort = "high";
        vm.AiSetupApiKey = SyntheticKey;
        Assert.True(vm.SaveAiSetupAndAnalyzeCommand.CanExecute(null));
        // An empty scan and no providers ensure this test can never make an AI request.
        vm.SaveAiSetupAndAnalyzeCommand.Execute(null);
        WaitUntil(() => !vm.IsAiSetupSaving && !vm.IsAiSetupOpen);

        var loaded = new AppSettingsStore().LoadAsync().GetAwaiter().GetResult();
        var restarted = CreateViewModel(loaded);
        Assert.Equal(deepSeek ? AiConnectionOption.DeepSeekId : AiConnectionOption.CustomId, restarted.SelectedAiConnectionId);
        Assert.Equal(AiProviderKind.OpenAiCompatible, restarted.SelectedProviderKind);
        Assert.Equal(deepSeek ? "deepseek-flash" : "retained-user-model", restarted.AiModel);
        Assert.Equal(deepSeek ? "medium" : "high", restarted.AiReasoningEffort);
        Assert.True(vm.ApiKey == SyntheticKey);
        Assert.False(vm.HasSettingsSaveError);
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Settings_can_configure_without_a_scan_and_return_to_the_editable_settings(bool deepSeek) => OnSta(() =>
    {
        using var data = new IsolatedData();
        var vm = CreateViewModel(AppSettings.Default());
        var changes = new List<string?>();
        vm.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        vm.IsSettingsOpen = true;
        Assert.Null(vm.ScanRoot);
        Assert.True(vm.ConfigureAiConnectionCommand.CanExecute(null));
        vm.ConfigureAiConnectionCommand.Execute(null);
        Assert.True(vm.IsAiSetupOpen);
        Assert.True(vm.ShowAiProviderChoices);
        Assert.False(vm.IsSettingsOpen);
        Assert.Equal("保存配置", vm.AiSetupSaveButtonText);
        if (deepSeek) vm.ChooseDeepSeekCommand.Execute(null);
        else vm.ChooseCustomAiCommand.Execute(null);
        vm.AiSetupEndpoint = "https://example.invalid/v1";
        vm.AiSetupModel = "settings-user-model";
        vm.AiSetupApiKey = SyntheticKey;
        vm.SaveAiSetupAndAnalyzeCommand.Execute(null);
        WaitUntil(() => !vm.IsAiSetupSaving && !vm.IsAiSetupOpen);

        Assert.True(vm.IsSettingsOpen);
        Assert.True(vm.HasAiConnectionConfiguration);
        Assert.False(vm.ShowAiConnectionSetupAction);
        Assert.False(vm.IsAnalyzing);
        Assert.Equal("AI 配置已保存。", vm.StatusText);
        Assert.Contains(nameof(vm.HasAiConnectionConfiguration), changes);
        Assert.Contains(nameof(vm.ShowAiConnectionSetupAction), changes);
        var restarted = CreateViewModel(new AppSettingsStore().LoadAsync().GetAwaiter().GetResult());
        Assert.True(restarted.HasAiConnectionConfiguration);
        Assert.Equal(deepSeek ? AiConnectionOption.DeepSeekId : AiConnectionOption.CustomId, restarted.SelectedAiConnectionId);
    });

    [Fact]
    public void Cancelling_settings_configuration_returns_to_settings_without_creating_a_configuration() => OnSta(() =>
    {
        using var data = new IsolatedData();
        var vm = CreateViewModel(AppSettings.Default());
        vm.IsSettingsOpen = true;
        vm.ConfigureAiConnectionCommand.Execute(null);
        vm.ChooseDeepSeekCommand.Execute(null);
        vm.AiSetupApiKey = SyntheticKey;
        vm.CloseAiSetupCommand.Execute(null);

        Assert.False(vm.IsAiSetupOpen);
        Assert.True(vm.IsSettingsOpen);
        Assert.False(vm.HasAiConnectionConfiguration);
        Assert.True(vm.ShowAiConnectionSetupAction);
        Assert.Equal(string.Empty, vm.AiSetupApiKey);
        Assert.False(File.Exists(Path.Combine(data.Path, "settings.json")));
    });

    [Fact]
    public void Saving_from_settings_with_a_scan_never_starts_analysis() => OnSta(() =>
    {
        using var data = new IsolatedData();
        var provider = new RecordingProvider();
        var vm = CreateViewModel(AppSettings.Default() with { AnalysisThresholdMb = 1 }, provider: provider);
        ApplyCompletedScan(vm);
        var analysisStarted = false;
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.IsAnalyzing) && vm.IsAnalyzing) analysisStarted = true;
        };
        vm.IsSettingsOpen = true;
        vm.ConfigureAiConnectionCommand.Execute(null);
        vm.ChooseDeepSeekCommand.Execute(null);
        vm.AiSetupApiKey = SyntheticKey;
        vm.SaveAiSetupAndAnalyzeCommand.Execute(null);
        WaitUntil(() => !vm.IsAiSetupSaving && !vm.IsAiSetupOpen);

        Assert.True(vm.IsSettingsOpen);
        Assert.False(analysisStarted);
        Assert.Equal(0, provider.Calls);
        Assert.Equal("AI 配置已保存。", vm.StatusText);
    });

    [Fact]
    public void Configuring_from_analyze_still_saves_and_runs_the_analysis() => OnSta(() =>
    {
        using var data = new IsolatedData();
        var provider = new RecordingProvider();
        var vm = CreateViewModel(AppSettings.Default() with { AnalysisThresholdMb = 1 }, provider: provider);
        ApplyCompletedScan(vm);
        vm.AnalyzeCommand.Execute(null);
        Assert.True(vm.IsAiSetupOpen);
        Assert.Equal("保存并分析", vm.AiSetupSaveButtonText);
        vm.ChooseDeepSeekCommand.Execute(null);
        vm.AiSetupApiKey = SyntheticKey;
        vm.SaveAiSetupAndAnalyzeCommand.Execute(null);
        WaitUntil(() => !vm.IsAiSetupSaving && !vm.IsAiSetupOpen);

        Assert.False(vm.IsSettingsOpen);
        Assert.True(provider.Calls > 0);
        Assert.False(vm.IsAnalyzing);
        Assert.True(vm.HasAiConnectionConfiguration);
    });

    private static AppSettings ConfiguredSettings(string endpoint) => AppSettings.Default() with
    {
        ProviderKind = AiProviderKind.OpenAiCompatible,
        AiEndpoint = endpoint,
        AiModel = "retained-user-model",
        AiReasoningEffort = "high",
    };

    private static MainWindowViewModel CreateViewModel(AppSettings settings, string key = SyntheticKey, IAiProvider? provider = null)
    {
        var policy = new WindowsLocationPolicy();
        return new MainWindowViewModel(
            new DirectoryScanner(), new WindowsFolderPickerService(), new DeletionPolicyService(policy),
            new ExplorerFileLocationService(), new RecycleBinService(),
            new AiAnalysisService(provider is null ? [] : [provider], new AiCandidateBuilder(policy), new AiBatchPlanner()),
            new AppSettingsStore(), new AppSecretStore(),
            settings with { HasCompletedExplorerGuide = true }, key);
    }

    private static void ApplyCompletedScan(MainWindowViewModel vm)
    {
        var file = new FileSystemNode(@"C:\Users\Alice\Downloads\sample.bin", "sample.bin", ".bin",
            4 * 1024 * 1024, FileAttributes.Normal, null, null, false, false, [], []);
        var root = new FileSystemNode(@"C:\Users\Alice\Downloads", "Downloads", string.Empty,
            file.SizeBytes, FileAttributes.Directory, null, null, true, false, [], [file]);
        typeof(MainWindowViewModel).GetMethod("ApplySnapshot", BindingFlags.Instance | BindingFlags.NonPublic,
            null, [typeof(FileSystemNode), typeof(string), typeof(string)], null)!.Invoke(vm, [root, null, null]);
    }

    private sealed class RecordingProvider : IAiProvider
    {
        public AiProviderKind Kind => AiProviderKind.OpenAiCompatible;
        public int Calls { get; private set; }

        public Task<IReadOnlyList<AiAnalysisResult>> AnalyzeAsync(AiBatch batch, AiProviderOptions options,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            IReadOnlyList<AiAnalysisResult> results = batch.Records.Select(record =>
                new AiAnalysisResult(record.Path, RiskLevel.Low, SuggestedAction.Keep, "Synthetic result", 0.9)).ToArray();
            return Task.FromResult(results);
        }
    }

    private static void SuspendDeferredSaves(MainWindowViewModel vm) =>
        typeof(MainWindowViewModel).GetField("_suspendSettingsSave", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(vm, true);

    private static void SaveSettings(MainWindowViewModel vm)
    {
        var task = (Task<bool>)typeof(MainWindowViewModel)
            .GetMethod("SaveSettingsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null)!;
        WaitUntil(() => task.IsCompleted);
        Assert.True(task.GetAwaiter().GetResult());
    }

    private static void PumpDispatcher() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

    private static void WaitUntil(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < timeout, "AI configuration operation timed out.");
            PumpDispatcher();
            Thread.Sleep(1);
        }
        PumpDispatcher();
    }

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "WPF AI configuration test timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class IsolatedData : IDisposable
    {
        private readonly string? _previousDirectory = Environment.GetEnvironmentVariable("DISKLENS_DATA_DIRECTORY");
        public string Path { get; } = System.IO.Path.Combine(
            Environment.GetEnvironmentVariable("DISKLENS_TEST_ARTIFACTS") is { Length: > 0 } artifactsDirectory
                ? artifactsDirectory
                : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DiskLens.App.Tests"),
            Guid.NewGuid().ToString("N"));

        public IsolatedData()
        {
            Directory.CreateDirectory(Path);
            Environment.SetEnvironmentVariable("DISKLENS_DATA_DIRECTORY", Path);
        }

        public void Dispose() => Environment.SetEnvironmentVariable("DISKLENS_DATA_DIRECTORY", _previousDirectory);
    }
}
