using System;
using System.Net.Http;
using System.Windows;
using DiskLens.App.Services;
using DiskLens.App.Services.SharedKnowledge;
using DiskLens.App.ViewModels;
using DiskLens.Core.Ai;
using DiskLens.Core.Policies;
using DiskLens.Core.Scanning;
using DiskLens.Core.Updates;

namespace DiskLens.App;

public partial class App : Application
{
    private HttpClient? _openAiCompatibleHttpClient;
    private HttpClient? _ollamaHttpClient;
    private ScanWorkspaceViewModel? _workspace;
    private HttpClient? _updateHttpClient;
    private UpdateCheckCoordinator? _updateCoordinator;
    private SharedKnowledgeRuntime? _sharedKnowledge;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var settingsStore = new AppSettingsStore();
        var secretStore = new AppSecretStore();
        var settings = await settingsStore.LoadAsync().ConfigureAwait(true);
        Localization.L.SetLanguage(settings.Language);
        var apiKey = await secretStore.LoadApiKeyAsync().ConfigureAwait(true);

        var folderPickerService = new WindowsFolderPickerService();
        var deletionPolicyService = new DeletionPolicyService(new WindowsLocationPolicy());
        var fileLocationService = new ExplorerFileLocationService();
        var recycleBinService = new RecycleBinService();
        var analysisMemoryStore = new AnalysisMemoryStore();
        _sharedKnowledge = new SharedKnowledgeRuntime(analysisMemoryStore);
        var openAiCompatibleHttpClient = _openAiCompatibleHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        var ollamaHttpClient = _ollamaHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10),
        };

        _workspace = new ScanWorkspaceViewModel(
            (taskSettings, taskApiKey) => new MainWindowViewModel(
                new DirectoryScanner(),
                folderPickerService,
                deletionPolicyService,
                fileLocationService,
                recycleBinService,
                new AiAnalysisService(
                [
                    new OpenAiCompatibleProvider(openAiCompatibleHttpClient),
                    new OllamaAiProvider(ollamaHttpClient),
                ],
                    new AiCandidateBuilder(new WindowsLocationPolicy()),
                    new AiBatchPlanner(),
                    analysisMemoryStore, _sharedKnowledge.Pipeline),
                settingsStore,
                secretStore,
                taskSettings,
                taskApiKey) { SharedRuntime = _sharedKnowledge },
            settings,
            apiKey,
            settingsStore,
            secretStore);

        _updateHttpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        _updateCoordinator = new UpdateCheckCoordinator(new ReleaseFeedClient(_updateHttpClient),
            new UpdatePreferenceStore(), typeof(App).Assembly.GetName().Version ?? new Version(1, 1, 0));
        var window = new MainWindow
        {
            DataContext = _workspace.ActiveViewModel,
            UpdateCoordinator = _updateCoordinator,
            SharedRuntime = _sharedKnowledge,
        };

        _updateCoordinator.UpdateAvailable += (_, release) =>
        {
            if (window.IsVisible) new UpdateAvailableWindow(release, _updateCoordinator) { Owner = window }.ShowDialog();
        };
        window.Closed += (_, _) => _updateCoordinator.Dispose();

        MainWindow = window;
        window.Show();
        await _updateCoordinator.CheckAtStartupAsync().ConfigureAwait(true);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _workspace?.Dispose();
        _updateCoordinator?.Dispose();
        _updateHttpClient?.Dispose();
        _openAiCompatibleHttpClient?.Dispose();
        _ollamaHttpClient?.Dispose();
        _sharedKnowledge?.Dispose();
        base.OnExit(e);
    }
}
