using DiskLens.Core.Policies;

namespace DiskLens.Tests.Policies;

public sealed class DeletionPolicyServiceTests
{
    private readonly WindowsLocationPolicy _locationPolicy = new();
    private readonly DeletionPolicyService _service;

    public DeletionPolicyServiceTests()
    {
        _service = new DeletionPolicyService(_locationPolicy);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\kernel32.dll", true)]
    [InlineData(@"C:\Program Files\Example\app.exe", true)]
    [InlineData(@"C:\Users\tester\Downloads\movie.mkv", false)]
    public void IsProtectedLocation_should_match_expected_windows_rules(string path, bool expected)
    {
        var actual = _locationPolicy.IsProtectedLocation(path);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(Environment.SpecialFolder.Windows)]
    [InlineData(Environment.SpecialFolder.ProgramFiles)]
    public void Protected_prefixes_should_match_their_roots_and_children_without_matching_similar_siblings(
        Environment.SpecialFolder folder)
    {
        var prefix = Environment.GetFolderPath(folder);
        if (string.IsNullOrWhiteSpace(prefix))
        {
            throw Xunit.Sdk.SkipException.ForSkip($"No local path is available for {folder}.");
        }

        prefix = prefix.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var child = Path.Combine(prefix, "DiskLens-policy-boundary-test", "sample.bin");

        Assert.True(_locationPolicy.IsProtectedLocation(prefix));
        Assert.True(_locationPolicy.IsProtectedLocation(prefix + Path.DirectorySeparatorChar));
        Assert.True(_locationPolicy.IsProtectedLocation(child.ToUpperInvariant()));
        Assert.True(_locationPolicy.IsProtectedLocation(child.ToLowerInvariant()));
        Assert.True(_locationPolicy.IsProtectedLocation(child.Replace(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        Assert.False(_locationPolicy.IsProtectedLocation(prefix + "X"));
        Assert.False(_locationPolicy.IsProtectedLocation(Path.Combine(prefix + "X", "sample.bin")));
    }

    [Theory]
    [InlineData(@"C:\ProgramData\Thunder Network", true)]
    [InlineData(@"C:\ProgramData\Thunder Network\settings.dat", false)]
    [InlineData(@"C:\Program Files\Example", true)]
    [InlineData(@"C:\Program Files (x86)\Example\cache.bin", false)]
    [InlineData(@"C:\Windows\Logs\CBS\CBS.log", false)]
    public void Evaluate_should_allow_confirmed_manual_recycle_in_protected_locations(string path, bool isDirectory)
    {
        var decision = _service.Evaluate(path, isDirectory);

        Assert.True(decision.CanRecycle);
        Assert.True(decision.CanOpenLocation);
        Assert.NotEmpty(decision.Reason);
    }

    [Theory]
    [InlineData(Environment.SpecialFolder.CommonApplicationData)]
    [InlineData(Environment.SpecialFolder.ProgramFiles)]
    [InlineData(Environment.SpecialFolder.Windows)]
    public void Manual_recycle_should_not_depend_on_location_risk_while_risk_tags_remain(Environment.SpecialFolder folder)
    {
        var prefix = Environment.GetFolderPath(folder);
        Assert.False(string.IsNullOrWhiteSpace(prefix));
        var path = Path.Combine(prefix, "DiskLens-policy-test", "cache.tmp");

        Assert.True(_service.Evaluate(path, isDirectory: false).CanRecycle);
        Assert.True(_locationPolicy.IsProtectedLocation(path));
        Assert.Contains("protected-location", _locationPolicy.GetRiskTags(path, false, FileAttributes.Normal));
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"D:/")]
    [InlineData(@"\\server\share")]
    [InlineData(@"\\server\share\")]
    public void Evaluate_should_block_recycling_filesystem_roots(string path)
    {
        var decision = _service.Evaluate(path, isDirectory: true);

        Assert.False(decision.CanRecycle);
        Assert.True(decision.CanOpenLocation);
        Assert.Contains("根目录", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_should_allow_recycling_a_child_directory_on_a_network_share()
    {
        var decision = _service.Evaluate(@"\\server\share\folder", isDirectory: true);

        Assert.True(decision.CanRecycle);
        Assert.True(decision.CanOpenLocation);
    }
    [Fact]
    public void Evaluate_should_block_recycling_when_path_is_blank()
    {
        var decision = _service.Evaluate("  ", isDirectory: false);

        Assert.False(decision.CanRecycle);
        Assert.False(decision.CanOpenLocation);
        Assert.NotEmpty(decision.Reason);
    }
    [Fact]
    public void AssessPath_should_return_protected_state_and_risk_tags_together()
    {
        var assessment = _locationPolicy.AssessPath(
            @"C:\Windows\Temp\sample.tmp",
            isDirectory: false,
            attributes: FileAttributes.Hidden | FileAttributes.System);

        Assert.True(assessment.IsProtectedLocation);
        Assert.Contains("protected-location", assessment.RiskTags);
        Assert.Contains("system", assessment.RiskTags);
        Assert.Contains("hidden", assessment.RiskTags);
        Assert.Contains("system-attribute", assessment.RiskTags);
        Assert.Contains("temporary", assessment.RiskTags);
    }
}
