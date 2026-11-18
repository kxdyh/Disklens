using System.IO;

namespace DiskLens.Tests.Ui;

public sealed class AiSetupFlowSourceTests
{
    [Fact]
    public void Analyze_action_should_open_provider_setup_when_no_provider_is_configured()
    {
        var viewModel = ReadRepoFile("src", "DiskLens.App", "ViewModels", "MainWindowViewModel.cs");

        Assert.Contains("AnalyzeCommand = new AsyncRelayCommand(AnalyzeOrConfigureAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("if (!IsAiProviderConfigured)", viewModel, StringComparison.Ordinal);
        Assert.Contains("OpenAiSetup();", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void DeepSeek_setup_should_use_defaults_and_link_to_the_official_api_key_page()
    {
        var viewModel = ReadRepoFile("src", "DiskLens.App", "ViewModels", "MainWindowViewModel.cs");
        var markup = ReadRepoFile("src", "DiskLens.App", "MainWindow.xaml");

        Assert.Contains("https://api.deepseek.com", viewModel, StringComparison.Ordinal);
        Assert.Contains("deepseek-flash", viewModel, StringComparison.Ordinal);
        Assert.Contains("AiSetupReasoningEffort = \"medium\"", viewModel, StringComparison.Ordinal);
        Assert.Contains("https://platform.deepseek.com/api_keys", markup, StringComparison.Ordinal);
        Assert.Contains("前往 DeepSeek 官网获取 API Key", markup, StringComparison.Ordinal);
        Assert.Contains("DeepSeekApiKeyPasswordBox", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Custom_setup_should_collect_endpoint_model_key_and_optional_reasoning_effort()
    {
        var markup = ReadRepoFile("src", "DiskLens.App", "MainWindow.xaml");
        var viewModel = ReadRepoFile("src", "DiskLens.App", "ViewModels", "MainWindowViewModel.cs");

        Assert.Contains("自定义 OpenAI 兼容接口", markup, StringComparison.Ordinal);
        Assert.Contains("接口基础地址", markup, StringComparison.Ordinal);
        Assert.Contains("AiSetupEndpoint", markup, StringComparison.Ordinal);
        Assert.Contains("AiSetupModel", markup, StringComparison.Ordinal);
        Assert.Contains("CustomApiKeyPasswordBox", markup, StringComparison.Ordinal);
        Assert.Contains("AiSetupReasoningEffort", markup, StringComparison.Ordinal);
        Assert.Contains("/chat/completions", viewModel, StringComparison.Ordinal);
        Assert.Contains("uri.IsLoopback", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void Ai_setup_should_not_close_or_start_analysis_after_dismissal_during_save()
    {
        var viewModel = ReadRepoFile("src", "DiskLens.App", "ViewModels", "MainWindowViewModel.cs");
        var closeStart = viewModel.IndexOf("private void CloseAiSetup()", StringComparison.Ordinal);
        var chooseStart = viewModel.IndexOf("private void ChooseDeepSeekSetup()", closeStart, StringComparison.Ordinal);
        var saveStart = viewModel.IndexOf("private async Task SaveAiSetupAndAnalyzeAsync()", StringComparison.Ordinal);
        var validateStart = viewModel.IndexOf("private static bool IsValidOpenAiCompatibleEndpoint", saveStart, StringComparison.Ordinal);
        Assert.True(closeStart >= 0 && chooseStart > closeStart && saveStart > chooseStart && validateStart > saveStart);

        Assert.Contains("if (IsAiSetupSaving)", viewModel[closeStart..chooseStart], StringComparison.Ordinal);
        Assert.Contains("if (!IsAiSetupOpen)", viewModel[saveStart..validateStart], StringComparison.Ordinal);
        Assert.Contains("await AnalyzeAsync().ConfigureAwait(true);", viewModel[saveStart..validateStart], StringComparison.Ordinal);
    }

    private static string ReadRepoFile(params string[] pathParts)
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        return LocalizedXamlSource.ReadAllText(Path.Combine([repoRoot, .. pathParts]));
    }
}
