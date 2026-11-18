using DiskLens.App.Infrastructure;
using DiskLens.Core.Models;

namespace DiskLens.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private string _settingsSection = "analysis";
    private bool _hideAiDisclaimer;
    private bool _disclaimerDismissed;
    private bool _isOfflineAlertVisible;
    public Func<AiAnalysisSubmissionPreview, CancellationToken, Task<bool>>? ConfirmAiSubmissionAsync { get; set; }
    public Func<long, int, CancellationToken, Task<bool>>? ConfirmBudgetContinuationAsync { get; set; }
    public RelayCommand SelectSettingsSectionCommand { get; private set; } = null!;
    public RelayCommand OpenAiAnalysisSettingsCommand { get; private set; } = null!;
    public RelayCommand DismissAiDisclaimerCommand { get; private set; } = null!;
    public RelayCommand DismissOfflineAlertCommand { get; private set; } = null!;

    public string SettingsSection
    {
        get => _settingsSection;
        set => SetProperty(ref _settingsSection, value);
    }
    public bool HideAiDisclaimer
    {
        get => _hideAiDisclaimer;
        set
        {
            if (!SetProperty(ref _hideAiDisclaimer, value)) return;
            OnPropertyChanged(nameof(ShowAiDisclaimerAlert));
            SaveSettingsDeferred();
        }
    }
    public bool ShowAiDisclaimerAlert => !_hideAiDisclaimer && !_disclaimerDismissed;
    public bool IsOfflineAlertVisible
    {
        get => _isOfflineAlertVisible;
        private set => SetProperty(ref _isOfflineAlertVisible, value);
    }
    private void InitializeSettingsNavigation()
    {
        System.ComponentModel.PropertyChangedEventManager.AddHandler(L.Current, OnPresentationLanguageChanged, nameof(L.Language));
        SelectSettingsSectionCommand = new RelayCommand(value => { if (value is string section) SettingsSection = section; });
        OpenAiAnalysisSettingsCommand = new RelayCommand(() => { SettingsSection = "analysis"; IsSettingsOpen = true; });
        DismissAiDisclaimerCommand = new RelayCommand(() => { _disclaimerDismissed = true; OnPropertyChanged(nameof(ShowAiDisclaimerAlert)); });
        DismissOfflineAlertCommand = new RelayCommand(() => IsOfflineAlertVisible = false);
    }
}
