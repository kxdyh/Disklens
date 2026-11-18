using System.IO;

namespace DiskLens.Tests.Ui;

public sealed class ScanRunnerControlSourceTests
{
    [Fact]
    public void ScanRunnerControl_should_use_explicit_ready_running_and_game_over_states()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var controlPath = Path.Combine(repoRoot, "src", "DiskLens.App", "Controls", "ScanRunnerControl.cs");
        var source = File.ReadAllText(controlPath);

        Assert.Contains("enum RunnerState", source, StringComparison.Ordinal);
        Assert.Contains("Ready", source, StringComparison.Ordinal);
        Assert.Contains("Running", source, StringComparison.Ordinal);
        Assert.Contains("GameOver", source, StringComparison.Ordinal);
        Assert.Contains("StartGame()", source, StringComparison.Ordinal);
        Assert.Contains("RestartGame()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ScanRunnerControl_should_not_auto_start_the_game_when_scanning_becomes_active()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var controlPath = Path.Combine(repoRoot, "src", "DiskLens.App", "Controls", "ScanRunnerControl.cs");
        var source = File.ReadAllText(controlPath);

        Assert.Contains("点击开始游戏后即可起跳躲避文件方块", source, StringComparison.Ordinal);
        Assert.Contains("重新开始", source, StringComparison.Ordinal);
        Assert.Contains("_currentState = RunnerState.Ready;", source, StringComparison.Ordinal);
        Assert.Contains("HandleIsActiveChanged(bool isActive)", source, StringComparison.Ordinal);
        Assert.Contains("_timer.Stop();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ScanRunnerControl_should_use_wider_obstacle_spacing_to_lower_frequency()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var controlPath = Path.Combine(repoRoot, "src", "DiskLens.App", "Controls", "ScanRunnerControl.cs");
        var source = File.ReadAllText(controlPath);

        Assert.Contains("ObstacleSpacingMinimum", source, StringComparison.Ordinal);
        Assert.Contains("ObstacleSpacingVariance", source, StringComparison.Ordinal);
        Assert.Contains("ObstacleSpacingSpeedFactor", source, StringComparison.Ordinal);
        Assert.Contains("ObstacleSpacingMinimum = 190", source, StringComparison.Ordinal);
    }
}
