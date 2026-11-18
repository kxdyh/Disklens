using DiskLens.Core.Networking;

namespace DiskLens.Tests.Networking;

public sealed class SystemNetworkAvailabilityTests
{
    [Fact]
    public void Losing_internet_while_virtual_adapters_stay_up_is_reported_as_offline()
    {
        var internet = true;
        var availability = new SystemNetworkAvailability(() => true, () => internet);
        Assert.True(availability.IsNetworkAvailable);
        internet = false;
        Assert.False(availability.IsNetworkAvailable);
        Assert.True(NetworkFailureClassifier.IsNetworkUnavailable("https://api.deepseek.com", availability));
        Assert.False(NetworkFailureClassifier.IsNetworkUnavailable("http://127.0.0.1:11434", availability));
        internet = true;
        Assert.True(availability.IsNetworkAvailable);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Unknown_internet_state_preserves_the_interface_fallback(bool link, bool expected)
        => Assert.Equal(expected, new SystemNetworkAvailability(() => link, () => null).IsNetworkAvailable);

    [Fact]
    public void A_disconnected_interface_cannot_be_overridden_by_a_stale_internet_hint()
        => Assert.False(new SystemNetworkAvailability(() => false, () => true).IsNetworkAvailable);

    [Theory]
    [InlineData("http://192.168.1.20:11434")]
    [InlineData("http://10.0.0.20:1234/v1")]
    [InlineData("http://172.16.1.20:1234/v1")]
    [InlineData("http://[fd00::20]:11434")]
    [InlineData("http://my-model.local:11434")]
    public void Local_network_models_remain_reachable_without_public_internet(string endpoint)
    {
        var localOnly = new SystemNetworkAvailability(() => true, () => false);
        Assert.False(NetworkFailureClassifier.IsNetworkUnavailable(endpoint, localOnly));
        Assert.True(NetworkFailureClassifier.IsNetworkUnavailable("https://api.deepseek.com", localOnly));
        Assert.False(NetworkFailureClassifier.IsOffline(new TimeoutException(), localOnly, endpoint));
        Assert.True(NetworkFailureClassifier.IsNetworkUnavailable(endpoint,
            new SystemNetworkAvailability(() => false, () => false)));
    }
}
