using System.Net;
using System.Net.Sockets;
using DiskLens.Core.Networking;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.Tests.SharedKnowledge;

public sealed partial class SharedPipelineTests
{
    [Fact]
    public async Task No_network_interface_prevents_cloud_queries_and_keeps_the_offline_reason()
    {
        var client = new Client();
        var resolution = await new SharedKnowledgePipeline(new Consent(), new Extractor(), client,
            networkAvailability: new Network(false)).ResolveAsync([Candidate]);
        Assert.Equal(0, client.Queries);
        Assert.True(resolution.Unavailable);
        Assert.True(resolution.NetworkUnavailable);
        Assert.Empty(resolution.MissingPaths);
    }

    [Fact]
    public async Task Local_shared_test_service_does_not_require_an_external_network()
    {
        var client = new Client();
        var resolution = await new SharedKnowledgePipeline(new Consent(), new Extractor(), client,
            networkAvailability: new Network(false), requiresNetwork: false).ResolveAsync([Candidate]);
        Assert.Equal(1, client.Queries);
        Assert.False(resolution.Unavailable);
        Assert.False(resolution.NetworkUnavailable);
        Assert.Single(resolution.ResultsByPath);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(401, false)]
    [InlineData(429, false)]
    [InlineData(503, false)]
    public async Task Cloud_transport_offline_and_HTTP_service_failures_remain_distinguishable(int status, bool offline)
    {
        Exception failure = status == 0
            ? new HttpRequestException("synthetic no route", new SocketException((int)SocketError.NetworkUnreachable))
            : new HttpRequestException("synthetic service failure", null, (HttpStatusCode)status);
        var client = new Client { OnQuery = () => throw failure };
        var resolution = await new SharedKnowledgePipeline(new Consent(), new Extractor(), client,
            networkAvailability: new Network(true)).ResolveAsync([Candidate]);
        Assert.Equal(1, client.Queries);
        Assert.True(resolution.Unavailable);
        Assert.Equal(offline, resolution.NetworkUnavailable);
        Assert.Empty(resolution.MissingPaths);
    }

    private sealed class Network(bool available) : INetworkAvailability
    { public bool IsNetworkAvailable => available; }
}
