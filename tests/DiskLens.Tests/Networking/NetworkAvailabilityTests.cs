using System.Net;
using System.Net.Sockets;
using DiskLens.Core.Networking;

namespace DiskLens.Tests.Networking;

public sealed class NetworkAvailabilityTests
{
    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(503)]
    public void Service_responses_are_never_classified_as_an_offline_computer(int status)
    {
        var error = new HttpRequestException("synthetic service error", new SocketException((int)SocketError.NetworkDown), (HttpStatusCode)status);
        Assert.False(NetworkFailureClassifier.IsOffline(error, new Network(false)));
    }

    [Theory]
    [InlineData(SocketError.NetworkDown, true)]
    [InlineData(SocketError.NetworkUnreachable, true)]
    [InlineData(SocketError.ConnectionRefused, false)]
    [InlineData(SocketError.HostNotFound, false)]
    [InlineData(SocketError.TimedOut, false)]
    public void Only_definite_network_failures_override_an_available_interface(SocketError socketError, bool offline)
    {
        var error = new HttpRequestException("synthetic transport failure", new SocketException((int)socketError));
        Assert.Equal(offline, NetworkFailureClassifier.IsOffline(error, new Network(true)));
    }

    [Fact]
    public void No_network_interface_classifies_transport_failures_but_not_local_file_errors_or_cancellation()
    {
        var network = new Network(false);
        Assert.True(NetworkFailureClassifier.IsOffline(new HttpRequestException("test offline"), network));
        Assert.False(NetworkFailureClassifier.IsOffline(new IOException("test local file failure"), network));
        Assert.False(NetworkFailureClassifier.IsOffline(new TaskCanceledException(), network));
        Assert.True(NetworkFailureClassifier.IsOffline(new TimeoutException(), network));
        Assert.False(NetworkFailureClassifier.IsOffline(new TimeoutException(), new Network(true)));
    }

    [Theory]
    [InlineData("http://localhost:11434", false)]
    [InlineData("http://127.0.0.1:1234/v1", false)]
    [InlineData("http://[::1]:11434", false)]
    [InlineData("https://api.deepseek.com", true)]
    [InlineData("invalid endpoint", false)]
    public void Offline_preflight_allows_loopback_services_and_preserves_endpoint_validation(string endpoint, bool blocked)
        => Assert.Equal(blocked, NetworkFailureClassifier.IsNetworkUnavailable(endpoint, new Network(false)));

    private sealed class Network(bool available) : INetworkAvailability
    { public bool IsNetworkAvailable => available; }
}
