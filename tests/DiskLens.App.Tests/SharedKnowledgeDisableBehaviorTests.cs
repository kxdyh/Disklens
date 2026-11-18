using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using DiskLens.Core.Networking;

namespace DiskLens.App.Tests;

public sealed partial class SharedKnowledgeDialogConsentBehaviorTests
{
    [Theory]
    [InlineData("01")]
    [InlineData("02")]
    [InlineData("03")]
    public void Small_confirmation_disables_real_consent_without_returning_to_the_onboarding(string theme) => OnSta(() =>
    {
        using var fixture = new Fixture(new DialogNetwork(true));
        EnableForEntryTest(fixture);
        var window = new SharedKnowledgeDisableWindow(fixture.Runtime, theme)
        {
            ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10_000, Top = -10_000
        };
        try
        {
            window.Show(); Pump();
            Assert.InRange(window.ActualWidth, 400, 500);
            Assert.InRange(window.ActualHeight, 150, 300);
            Assert.Null(window.FindName("IntroPage"));
            ((Button)window.FindName("DisableSharingButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitUntil(() => !window.IsVisible);
            Assert.False(fixture.Store.GetSnapshotAsync().GetAwaiter().GetResult().CanShare);
            Assert.Equal(1, fixture.Client.UpdateCalls);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Losing_network_before_enabling_keeps_sharing_off_and_does_not_register() => OnSta(() =>
    {
        using var fixture = new Fixture(new DialogNetwork(false));
        var dialog = fixture.Open();
        try
        {
            Preview(dialog);
            ReadAgreement(dialog);
            Check(dialog, "AcceptAgreement").IsChecked = true;
            Check(dialog, "AcceptFuture").IsChecked = true;
            Invoke(dialog, "Save_OnClick");
            WaitUntil(() => !Busy(dialog));
            Assert.False(fixture.Store.GetSnapshotAsync().GetAwaiter().GetResult().CanShare);
            Assert.Equal(0, fixture.Client.RegisterCalls);
            Assert.Equal(NetworkFailureClassifier.OfflineMessage, ((TextBlock)dialog.FindName("FeedbackText")).Text);
        }
        finally { dialog.Close(); }
    });

    private static void EnableForEntryTest(Fixture fixture)
    {
        var task = fixture.Store.SaveAsync(new DiskLens.App.Services.SharedKnowledge.SharedPreferences(
            Enabled: true, AgreementVersion: DiskLens.Core.SharedKnowledge.SharedConsentSnapshot.CurrentAgreementVersion,
            EnabledAt: DateTimeOffset.UtcNow.AddMinutes(-1), Epoch: 0, PolicyRevision: "entry-test",
            Installation: new("synthetic-installation", "synthetic-secret", 0)));
        WaitUntil(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }

    private sealed class DialogNetwork(bool available) : INetworkAvailability
    { public bool IsNetworkAvailable => available; }
}
