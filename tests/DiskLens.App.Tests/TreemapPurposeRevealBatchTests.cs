using DiskLens.App.Models;

namespace DiskLens.App.Tests;

public sealed class TreemapPurposeRevealBatchTests
{
    [Fact]
    public void Background_completion_waits_for_the_actual_visible_purpose()
    {
        var batch = new TreemapPurposeRevealBatch();
        batch.Enqueue(@"C:\Users", "用户资料");
        batch.AdvanceTo(0);
        batch.AdvanceTo(90_000);
        batch.RevealVisible([new(@"C:\Users", "旧的用途")]);
        Assert.False(batch.HasRunningReveals);
        Assert.Equal(0, batch.GetProgress(@"C:\Users", "用户资料"));
        batch.RevealVisible([new(@"c:\users", "用户资料")]);
        Assert.True(batch.HasRunningReveals);
        Assert.Equal(0, batch.GetProgress(@"C:\Users", "用户资料"));
        batch.AdvanceTo(90_160);
        Assert.InRange(batch.GetProgress(@"C:\Users", "用户资料"), .8, .9);
    }

    [Fact]
    public void Leaving_the_task_pauses_mid_reveal_and_coming_back_resumes_without_skipping()
    {
        var batch = new TreemapPurposeRevealBatch();
        batch.Enqueue(@"C:\Users", "用户资料");
        batch.AdvanceTo(0);
        batch.RevealVisible([new(@"C:\Users", "用户资料")]);
        batch.AdvanceTo(80);
        var beforeLeaving = batch.GetProgress(@"C:\Users", "用户资料");
        batch.Pause();
        batch.AdvanceTo(90_000);
        Assert.Equal(beforeLeaving, batch.GetProgress(@"C:\Users", "用户资料"));
        batch.AdvanceTo(90_240);
        Assert.Equal(1, batch.GetProgress(@"C:\Users", "用户资料"));
        Assert.False(batch.HasRunningReveals);
    }

    [Fact]
    public void Only_visible_paths_start_and_their_reveal_order_is_staggered()
    {
        var batch = new TreemapPurposeRevealBatch();
        foreach (var path in new[] { @"C:\one", @"C:\two", @"C:\nested\three" })
            batch.Enqueue(path, "文件用途");
        batch.AdvanceTo(0);
        batch.RevealVisible([new(@"C:\two", "文件用途"), new(@"C:\one", "文件用途")]);
        batch.AdvanceTo(80);
        Assert.True(batch.GetProgress(@"C:\two", "文件用途") > 0);
        Assert.Equal(0, batch.GetProgress(@"C:\one", "文件用途"));
        batch.AdvanceTo(1000);
        Assert.Equal(0, batch.GetProgress(@"C:\nested\three", "文件用途"));
        batch.RevealVisible([new(@"C:\nested\three", "文件用途")]);
        Assert.Equal(0, batch.GetProgress(@"C:\nested\three", "文件用途"));
        batch.AdvanceTo(1320);
        Assert.Equal(1, batch.GetProgress(@"C:\nested\three", "文件用途"));
    }

    [Fact]
    public void Completed_purposes_do_not_replay_and_reduced_motion_also_completes_offscreen_items()
    {
        var batch = new TreemapPurposeRevealBatch();
        batch.Enqueue(@"C:\one", "文件用途");
        batch.CompleteAll();
        batch.Enqueue(@"C:\one", "文件用途");
        Assert.Equal(1, batch.GetProgress(@"C:\one", "文件用途"));
        Assert.False(batch.HasRunningReveals);
        batch.Clear();
        batch.Enqueue(@"C:\one", "文件用途");
        Assert.Equal(0, batch.GetProgress(@"C:\one", "文件用途"));
        Assert.Equal(1, batch.GetProgress(@"C:\cloud", "既有云端用途"));
    }
}
