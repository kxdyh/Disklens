using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using DiskLens.Core.Networking;

namespace DiskLens.App.Tests;

public sealed partial class SharedContributionOutboxBehaviorTests
{
    [Fact]
    public async Task No_network_interface_prevents_upload_and_returns_a_structured_offline_receipt()
    {
        using var outbox = Create(new UploadNetwork(false));
        await outbox.EnqueueAsync(_consent.Value, [Queued("offline-preflight-observation")]);

        var receipt = await outbox.FlushAsync();

        Assert.True(receipt.NetworkUnavailable);
        Assert.Empty(_client.Batches);
        Assert.Equal(1, receipt.Pending);
        Assert.Equal(NetworkFailureClassifier.OfflineMessage, outbox.LastError);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(429, false)]
    [InlineData(503, false)]
    public async Task Lost_network_is_distinguishable_from_upload_service_errors(int status, bool offline)
    {
        _client.Response = (_, _, _) => throw status switch
        {
            0 => new HttpRequestException("synthetic network down", new SocketException((int)SocketError.NetworkDown)),
            _ => new HttpRequestException("synthetic service failure", null, (HttpStatusCode)status),
        };
        using var outbox = Create(new UploadNetwork(true));
        await outbox.EnqueueAsync(_consent.Value, [Queued("offline-upload-observation")]);

        var receipt = await outbox.FlushAsync();

        Assert.Equal(offline, receipt.NetworkUnavailable);
        Assert.Equal(1, receipt.Pending);
        Assert.Single(_client.Batches);
        if (offline) Assert.Equal(NetworkFailureClassifier.OfflineMessage, outbox.LastError);
        else Assert.Contains("稍后重试", outbox.LastError);
    }

    private sealed class UploadNetwork(bool available) : INetworkAvailability
    { public bool IsNetworkAvailable => available; }
}
