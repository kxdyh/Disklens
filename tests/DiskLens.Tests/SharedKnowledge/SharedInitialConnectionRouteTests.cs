using System.Net;
using System.Net.Sockets;
using System.Text;
using DiskLens.Core.SharedKnowledge;

namespace DiskLens.Tests.SharedKnowledge;

public sealed class SharedInitialConnectionRouteTests
{
    private static readonly IPAddress Preferred = IPAddress.Parse("104.16.1.1");
    private static readonly IPAddress Alternate = IPAddress.Parse("172.64.1.1");
    private static readonly IPAddress Origin = IPAddress.Parse("104.18.1.1");
    private static readonly Uri Official = new("https://api.disklens.site/");

    [Theory]
    [InlineData("198.18.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("192.168.1.1")]
    [InlineData("1.1.1.1")]
    [InlineData("::1")]
    [InlineData("fc00::1")]
    [InlineData("::ffff:104.16.1.1")]
    public void Candidate_dns_rejects_non_edge_private_and_tunnel_addresses(string address)
        => Assert.False(SharedPreferredRouteDiscovery.IsCloudflareEdge(IPAddress.Parse(address)));

    [Theory]
    [InlineData("104.16.0.0")]
    [InlineData("104.23.255.255")]
    [InlineData("172.64.1.1")]
    [InlineData("2606:4700::1")]
    public void Candidate_dns_accepts_published_edge_ranges(string address)
        => Assert.True(SharedPreferredRouteDiscovery.IsCloudflareEdge(IPAddress.Parse(address)));

    [Fact]
    public async Task Preferred_probe_preserves_original_https_origin_and_is_reused()
    {
        var calls = new List<(IPAddress? Address, string Path, HttpMethod Method)>();
        using var route = CreateRoute(resolve: (host, _) => Task.FromResult(host == SharedPreferredRouteDiscovery.CandidateDnsHost
                ? new[] { Preferred } : host == "icook.tw" ? new[] { Alternate } : Array.Empty<IPAddress>()),
            candidateDnsSources: [SharedPreferredRouteDiscovery.CandidateDnsHost, "icook.tw"],
            send: async (address, request, token) =>
        {
            calls.Add((address, request.RequestUri!.AbsolutePath, request.Method));
            Assert.Equal(Official.Host, request.RequestUri.Host);
            Assert.Equal("https", request.RequestUri.Scheme);
            if (request.Method == HttpMethod.Get && address == Alternate)
                await Task.Delay(30, token);
            return Rules();
        });
        await route.EnsureInitializedAsync(default);
        await route.EnsureInitializedAsync(default);
        using var http = new HttpMessageInvoker(route, false);
        using var reply = await http.SendAsync(new(HttpMethod.Post, new Uri(Official, "v1/installations")), default);
        Assert.Equal("preferred-dns", route.SelectedSource);
        Assert.Equal(Preferred, route.SelectedAddress);
        Assert.Equal(3, calls.Count);
        Assert.Contains((Preferred, "/v1/rules", HttpMethod.Get), calls);
        Assert.Contains((Preferred, "/v1/installations", HttpMethod.Post), calls);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("invalid")]
    [InlineData("error")]
    [InlineData("timeout")]
    public async Task Bad_candidate_dns_falls_back_without_waiting_for_unbounded_discovery(string mode)
    {
        var probeAddresses = new List<IPAddress?>();
        async Task<IPAddress[]> Resolve(string host, CancellationToken token)
        {
            if (host != SharedPreferredRouteDiscovery.CandidateDnsHost) return [Origin, Alternate];
            if (mode == "timeout") await Task.Delay(Timeout.Infinite, token);
            if (mode == "error") throw new SocketException((int)SocketError.HostNotFound);
            return mode == "invalid" ? [IPAddress.Parse("198.18.0.1")] : [];
        }
        using var route = CreateRoute(resolve: Resolve, send: (address, _, _) =>
        {
            probeAddresses.Add(address);
            return Task.FromResult(Rules());
        });
        await route.EnsureInitializedAsync(default).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("origin-dns", route.SelectedSource);
        Assert.Equal(Origin, route.SelectedAddress);
        Assert.Equal(2, probeAddresses.Count);
        Assert.Contains(Origin, probeAddresses);
        Assert.Contains(Alternate, probeAddresses);
    }

    [Theory]
    [InlineData("https://custom.example/")]
    [InlineData("https://api.disklens.site/custom/")]
    [InlineData("https://api.disklens.site:8443/")]
    public async Task Custom_endpoints_never_query_candidate_dns(string endpoint)
    {
        var names = new List<string>();
        using var route = CreateRoute(new Uri(endpoint), resolve: (host, _) =>
        {
            names.Add(host);
            return Task.FromResult(new[] { Origin });
        });
        await route.EnsureInitializedAsync(default);
        Assert.DoesNotContain(SharedPreferredRouteDiscovery.CandidateDnsHost, names);
    }

    [Fact]
    public async Task Proxy_bypasses_candidate_discovery_and_change_back_to_direct_reselects()
    {
        var direct = false;
        var names = new List<string>();
        using var route = CreateRoute(capture: _ => new(new WebProxy(), direct ? "direct" : "proxy", direct),
            resolve: (host, _) => { names.Add(host); return Task.FromResult(new[] { Preferred, Alternate }); });
        await route.EnsureInitializedAsync(default);
        Assert.Empty(names);
        Assert.Equal("proxy", route.SelectedSource);
        direct = true;
        await route.EnsureInitializedAsync(default);
        Assert.Contains(SharedPreferredRouteDiscovery.CandidateDnsHost, names);
        Assert.Equal("preferred-dns", route.SelectedSource);
    }

    [Theory]
    [InlineData("tls")]
    [InlineData("redirect")]
    [InlineData("unavailable")]
    [InlineData("html")]
    [InlineData("json")]
    public async Task Failed_race_probes_select_origin_resolver_without_replaying_a_write(string failure)
    {
        var probes = 0;
        using var route = CreateRoute(send: (_, request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/v1/rules", request.RequestUri!.AbsolutePath);
            probes++;
            if (failure == "tls") throw new HttpRequestException("synthetic TLS failure");
            var response = Rules();
            if (failure == "redirect") response.StatusCode = HttpStatusCode.Redirect;
            if (failure == "unavailable") response.StatusCode = HttpStatusCode.ServiceUnavailable;
            if (failure == "html") response.Content = new StringContent("<html>not the API</html>");
            if (failure == "json") response.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            return Task.FromResult(response);
        });
        await route.EnsureInitializedAsync(default);
        Assert.Equal(1, probes);
        Assert.False(route.SwitchedAddress);
        Assert.Null(route.SelectedAddress);
        Assert.Equal("origin-resolver", route.SelectedSource);
    }

    [Theory]
    [InlineData("{\"ruleVersion\":\"not-a-version\",\"agreementVersion\":\"test\",\"rules\":[]}")]
    [InlineData("{\"ruleVersion\":-1,\"agreementVersion\":\"test\",\"rules\":[]}")]
    [InlineData("{\"ruleVersion\":1,\"rules\":[]}")]
    [InlineData("{\"ruleVersion\":1,\"agreementVersion\":\"test\",\"rules\":[null]}")]
    public async Task Preferred_probe_rejects_non_protocol_json(string json)
    {
        using var route = CreateRoute(send: (_, _, _) => Task.FromResult(Json(json)));
        await route.EnsureInitializedAsync(default);
        Assert.Null(route.SelectedAddress);
        Assert.Equal("origin-resolver", route.SelectedSource);
    }

    [Fact]
    public async Task Failed_initial_race_keeps_normal_business_deadline_for_origin()
    {
        var firstCanceled = false;
        var calls = 0;
        using var route = CreateRoute(send: async (address, request, token) =>
        {
            calls++;
            if (request.Method == HttpMethod.Get)
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { firstCanceled = true; throw; }
            }
            Assert.Null(address);
            Assert.Equal(HttpMethod.Post, request.Method);
            await Task.Delay(180, token);
            return Registration();
        });
        using var client = new SharedExperienceClient(Official, route);
        var installation = await client.RegisterAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.NotNull(installation);
        Assert.True(firstCanceled);
        Assert.Equal(2, calls);
        Assert.Null(route.SelectedAddress);
        Assert.Equal("origin-resolver", route.SelectedSource);
    }

    [Fact]
    public async Task Failed_write_is_not_replayed_and_next_manual_attempt_uses_origin()
    {
        var writes = new List<IPAddress?>();
        var diagnostics = new List<SharedRequestDiagnostic>();
        using var route = CreateRoute(send: (address, request, _) =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Rules());
            writes.Add(address);
            if (address is not null) throw new HttpRequestException("synthetic connection failure");
            return Task.FromResult(Registration());
        });
        using var client = new SharedExperienceClient(Official, route);
        client.RequestCompleted += diagnostics.Add;
        await Assert.ThrowsAsync<HttpRequestException>(() => client.RegisterAsync());
        Assert.Equal(new[] { Preferred }, writes);
        Assert.Equal("origin-resolver", route.SelectedSource);
        Assert.Equal("preferred-dns", Assert.Single(diagnostics).RouteSource);
        Assert.Equal(Preferred.ToString(), diagnostics[0].RouteAddress);
        Assert.Equal(SharedPreferredRouteDiscovery.CandidateDnsHost, diagnostics[0].RouteDnsHost);
        await client.RegisterAsync();
        Assert.Equal(2, writes.Count);
        Assert.Null(writes[1]);
        Assert.Equal("origin-resolver", diagnostics[1].RouteSource);
    }

    [Theory]
    [InlineData("unavailable")]
    [InlineData("json")]
    [InlineData("stream")]
    public async Task Failed_manual_matching_does_not_retry_and_next_manual_request_uses_origin(string failure)
    {
        var queries = new List<IPAddress?>();
        using var route = CreateRoute(send: (address, request, _) =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Rules());
            queries.Add(address);
            if (address is null) return Task.FromResult(Json("{\"items\":[]}"));
            if (failure == "unavailable") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            if (failure == "stream") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = BrokenBody() });
            return Task.FromResult(Json("invalid-json"));
        });
        using var client = new SharedExperienceClient(Official, route);
        Task Match() => client.QueryAsync(new("installation", "synthetic", 1), 1,
            [new("android-sdk", "36", "sdk-temp", "cache-only-v1")], default, retryTransientFailure: false);
        await Assert.ThrowsAnyAsync<Exception>(Match);
        Assert.Single(queries);
        Assert.Equal("origin-resolver", route.SelectedSource);
        await Match();
        Assert.Equal(2, queries.Count);
        Assert.Null(queries[1]);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Business_rejections_do_not_demote_a_healthy_edge(HttpStatusCode status)
    {
        using var route = CreateRoute(send: (_, request, _) => Task.FromResult(request.Method == HttpMethod.Get
            ? Rules() : new HttpResponseMessage(status)));
        using var client = new SharedExperienceClient(Official, route);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.RegisterAsync());
        Assert.Equal("preferred-dns", route.SelectedSource);
    }

    [Fact]
    public async Task Intentional_contributor_pause_is_not_a_route_failure()
    {
        using var route = CreateRoute(send: (_, request, _) =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Rules());
            var paused = Json("{\"error\":\"new_contributors_paused\"}");
            paused.StatusCode = HttpStatusCode.ServiceUnavailable;
            return Task.FromResult(paused);
        });
        using var client = new SharedExperienceClient(Official, route);
        await Assert.ThrowsAsync<SharedNewContributorsPausedException>(() => client.RegisterAsync());
        Assert.Equal("preferred-dns", route.SelectedSource);
    }

    [Fact]
    public async Task Caller_cancellation_does_not_demote_the_selected_edge()
    {
        using var route = CreateRoute(send: async (_, request, token) =>
        {
            if (request.Method == HttpMethod.Get) return Rules();
            await Task.Delay(Timeout.Infinite, token);
            return Registration();
        });
        using var client = new SharedExperienceClient(Official, route);
        await route.EnsureInitializedAsync(default);
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.RegisterAsync(cancel.Token));
        Assert.Equal("preferred-dns", route.SelectedSource);
    }

    [Fact]
    public async Task Body_read_deadline_does_not_retry_manual_matching_and_next_request_uses_origin()
    {
        var queries = new List<IPAddress?>();
        using var route = CreateRoute(send: (address, request, _) =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Rules());
            queries.Add(address);
            if (address is null) return Task.FromResult(Json("{\"items\":[]}"));
            var content = new StreamContent(new StalledStream());
            content.Headers.ContentType = new("application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var client = new SharedExperienceClient(Official, route, TimeSpan.FromMilliseconds(40));
        Task Match() => client.QueryAsync(new("installation", "synthetic", 1), 1,
            [new("android-sdk", "36", "sdk-temp", "cache-only-v1")], default, retryTransientFailure: false);
        await Assert.ThrowsAsync<SharedRequestTimeoutException>(Match);
        Assert.Single(queries);
        Assert.Equal("origin-resolver", route.SelectedSource);
        await Match();
        Assert.Equal(2, queries.Count);
        Assert.Null(queries[1]);
    }

    [Fact]
    public async Task Old_response_body_failure_cannot_demote_a_new_network_transport()
    {
        var bodyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBody = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var newNetwork = false;
        SharedRequestDiagnostic? diagnostic = null;
        using var route = CreateRoute(resolve: (_, _) => Task.FromResult(newNetwork
            ? new[] { Alternate, Preferred } : new[] { Preferred, Alternate }), send: (_, request, _) =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Rules());
            var content = new StreamContent(new DeferredBrokenStream(bodyStarted, releaseBody));
            content.Headers.ContentType = new("application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        });
        using var client = new SharedExperienceClient(Official, route);
        client.RequestCompleted += value => diagnostic = value;
        var query = client.QueryAsync(new("installation", "synthetic", 1), 1,
            [new("android-sdk", "36", "sdk-temp", "cache-only-v1")], default, retryTransientFailure: false);
        await bodyStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        newNetwork = true;
        route.InvalidateForNetworkChange();
        await route.EnsureInitializedAsync(default);
        Assert.Equal(Alternate, route.SelectedAddress);
        releaseBody.SetResult();
        await Assert.ThrowsAsync<IOException>(() => query);
        Assert.Equal("preferred-dns", route.SelectedSource);
        Assert.Equal(Alternate, route.SelectedAddress);
        Assert.Equal(Preferred.ToString(), diagnostic!.RouteAddress);
    }

    [Fact]
    public async Task Existing_read_retry_uses_origin_without_rediscovering_failed_candidates()
    {
        var reads = new List<IPAddress?>();
        using var route = CreateRoute(send: (address, request, _) =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/rules") return Task.FromResult(Rules());
            reads.Add(address);
            return Task.FromResult(address is not null ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : Json("{\"epoch\":1,\"enabled\":true,\"agreementVersion\":\"test\",\"shareHistory\":false,\"policyRevision\":\"v1\"}"));
        });
        using var client = new SharedExperienceClient(Official, route);
        await client.GetConsentAsync(new("installation", "synthetic", 1));
        Assert.Equal(2, reads.Count);
        Assert.Equal(Preferred, reads[0]);
        Assert.Null(reads[1]);
    }

    [Fact]
    public async Task Real_network_change_clears_origin_fallback_and_discovers_again()
    {
        var lookups = 0;
        var fail = true;
        using var route = CreateRoute(resolve: (host, _) =>
        {
            if (host == SharedPreferredRouteDiscovery.CandidateDnsHost) lookups++;
            return Task.FromResult(new[] { Preferred, Alternate });
        }, send: (_, request, _) =>
        {
            if (request.Method == HttpMethod.Get) return Task.FromResult(Rules());
            if (fail) throw new HttpRequestException("synthetic failure");
            return Task.FromResult(Registration());
        });
        using var client = new SharedExperienceClient(Official, route);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.RegisterAsync());
        Assert.Equal("origin-resolver", route.SelectedSource);
        Assert.Equal(1, lookups);
        fail = false;
        route.InvalidateForNetworkChange();
        await client.RegisterAsync();
        Assert.Equal(2, lookups);
        Assert.Equal("preferred-dns", route.SelectedSource);
    }

    [Fact]
    public async Task Parallel_sources_have_one_shared_budget_and_only_contact_the_api_over_http()
    {
        using var route = new SharedInitialConnectionRoute(Official, async (host, token) =>
        {
            if (host == "slow.example") { await Task.Delay(Timeout.Infinite, token); return []; }
            return host == "valid.example" ? [Preferred] : [IPAddress.Parse("198.18.0.1")];
        }, _ => new(new WebProxy(), "direct", true), (address, _) => new Handler((request, _) =>
        {
            Assert.Equal(Official.Host, request.RequestUri!.Host);
            Assert.Equal(Preferred, address);
            return Task.FromResult(Rules());
        }), discoveryTimeout: TimeSpan.FromMilliseconds(40), initialTimeout: TimeSpan.FromMilliseconds(300),
            candidateRotation: 0, candidateDnsSources: ["slow.example", "valid.example", "fake.example"]);
        await route.EnsureInitializedAsync(default).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("valid.example", route.SelectedDnsHost);
        Assert.Collection(route.DiscoveredSources,
            source => Assert.Equal("timeout", source.Outcome),
            source => Assert.Equal("ok", source.Outcome),
            source => Assert.Equal("no-cloudflare-address", source.Outcome));
    }

    [Fact]
    public async Task Fake_ip_results_fall_back_to_original_resolver_without_pinning_them()
    {
        var probes = 0;
        using var route = CreateRoute(resolve: (_, _) => Task.FromResult(new[]
        { IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.2") }),
            send: (_, _, _) => { probes++; return Task.FromResult(Rules()); });
        await route.EnsureInitializedAsync(default);
        Assert.Equal(0, probes);
        Assert.Null(route.SelectedAddress);
        Assert.Equal("origin-resolver", route.SelectedSource);
    }

    [Fact]
    public void Candidate_rotation_reaches_new_records_and_never_selects_more_than_two()
    {
        var addresses = new[] { Preferred, Alternate, Origin };
        var selected = Enumerable.Range(0, addresses.Length)
            .SelectMany(rotation => SharedPreferredRouteDiscovery.SelectCandidates(addresses, rotation)).Distinct().ToArray();
        Assert.Equal(3, selected.Length);
        Assert.All(Enumerable.Range(0, addresses.Length), rotation =>
            Assert.Equal(2, SharedPreferredRouteDiscovery.SelectCandidates(addresses, rotation).Length));
    }

    [Fact]
    public void Sources_with_many_records_do_not_dominate_and_alternate_prefers_another_source()
    {
        var sources = new[]
        {
            (Host: "small.example", Addresses: new[] { Preferred }),
            (Host: "large.example", Addresses: Enumerable.Range(1, 21)
                .Select(index => IPAddress.Parse($"172.64.1.{index}")).ToArray())
        };
        Assert.Equal("small.example", SharedPreferredRouteDiscovery.SelectSourceCandidates(sources, 0)[0].Host);
        Assert.Equal("large.example", SharedPreferredRouteDiscovery.SelectSourceCandidates(sources, 1)[0].Host);
        foreach (var rotation in new[] { 0, 1 })
        {
            var candidates = SharedPreferredRouteDiscovery.SelectSourceCandidates(sources, rotation);
            Assert.Equal(2, candidates.Length);
            Assert.NotEqual(candidates[0].Host, candidates[1].Host);
        }
    }

    [Fact]
    public void Alternate_prefers_a_different_network_and_duplicate_addresses_are_never_selected_twice()
    {
        var candidates = SharedPreferredRouteDiscovery.SelectSourceCandidates(new[]
        {
            ("first.example", new[] { Preferred }),
            ("duplicate.example", new[] { Preferred, IPAddress.Parse("104.16.1.2") }),
            ("other.example", new[] { Alternate })
        }, 0);
        Assert.Equal(Preferred, candidates[0].Address);
        Assert.Equal(Alternate, candidates[1].Address);
    }

    [Fact]
    public void Candidate_configuration_limits_sources_and_keeps_only_dns_hostnames()
    {
        var sources = SharedPreferredRouteDiscovery.NormalizeSources(new[]
        { "https://untrusted.example/path", "198.18.0.1", "localhost", "FIRST.example.", "first.example" }
            .Concat(Enumerable.Range(0, 12).Select(index => $"source{index}.example")));
        Assert.Equal(8, sources.Length);
        Assert.Equal("first.example", sources[0]);
        Assert.DoesNotContain("198.18.0.1", sources);
    }

    [Fact]
    public async Task One_failed_probe_does_not_end_the_race_and_fast_complete_origin_can_win()
    {
        using var route = CreateRoute(resolve: (host, _) => Task.FromResult(host == Official.Host
            ? new[] { Origin } : new[] { Preferred }), send: async (address, _, token) =>
        {
            if (address == Preferred) throw new HttpRequestException("unreachable candidate");
            await Task.Delay(25, token);
            return Rules();
        });
        await route.EnsureInitializedAsync(default);
        Assert.Equal("origin-dns", route.SelectedSource);
        Assert.Equal(Origin, route.SelectedAddress);
    }

    [Fact]
    public async Task Race_uses_at_most_six_deduplicated_probes_for_eight_configured_sources()
    {
        var probes = new System.Collections.Concurrent.ConcurrentBag<IPAddress?>();
        var sources = Enumerable.Range(0, 8).Select(index => $"source{index}.example").ToArray();
        using var route = CreateRoute(resolve: (host, _) => Task.FromResult(host == Official.Host
            ? new[] { Origin, Alternate } : new[] { IPAddress.Parse($"104.16.{Array.IndexOf(sources, host) + 1}.1") }),
            candidateDnsSources: sources, send: async (address, _, token) =>
            {
                probes.Add(address);
                await Task.Delay(Timeout.Infinite, token);
                return Rules();
            });
        await route.EnsureInitializedAsync(default);
        Assert.Equal(6, probes.Count);
        Assert.Equal(6, probes.Distinct().Count());
        Assert.Equal("origin-resolver", route.SelectedSource);
    }

    [Fact]
    public async Task Stubborn_loser_cannot_hold_the_global_deadline_or_keep_a_handler_alive()
    {
        var never = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        TrackingHandler? handler = null;
        using var route = new SharedInitialConnectionRoute(Official,
            (host, _) => Task.FromResult(host == Official.Host ? Array.Empty<IPAddress>() : new[] { Preferred }),
            _ => new(new WebProxy(), "direct", true), (address, _) =>
            {
                var created = new TrackingHandler((_, _) => never.Task);
                if (address is not null) handler = created;
                return created;
            },
            discoveryTimeout: TimeSpan.FromMilliseconds(20), initialTimeout: TimeSpan.FromMilliseconds(80),
            candidateRotation: 0, candidateDnsSources: ["candidate.example"]);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        await route.EnsureInitializedAsync(default).WaitAsync(TimeSpan.FromMilliseconds(700));
        Assert.True(elapsed.Elapsed < TimeSpan.FromMilliseconds(700));
        Assert.True(handler!.Disposed);
        Assert.Equal("origin-resolver", route.SelectedSource);
        never.TrySetResult(Rules());
    }

    [Fact]
    public async Task Caller_cancellation_disposes_all_probe_pools_and_does_not_commit_a_route()
    {
        var handlers = new System.Collections.Concurrent.ConcurrentBag<TrackingHandler>();
        using var route = new SharedInitialConnectionRoute(Official,
            (host, _) => Task.FromResult(host == Official.Host ? new[] { Origin, Alternate } : new[] { Preferred }),
            _ => new(new WebProxy(), "direct", true), (_, _) =>
            {
                var handler = new TrackingHandler(async (_, token) =>
                { await Task.Delay(Timeout.Infinite, token); return Rules(); });
                handlers.Add(handler);
                return handler;
            }, discoveryTimeout: TimeSpan.FromMilliseconds(20), initialTimeout: TimeSpan.FromSeconds(1),
            candidateRotation: 0, candidateDnsSources: ["candidate.example"]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => route.EnsureInitializedAsync(cancellation.Token));
        Assert.All(handlers, handler => Assert.True(handler.Disposed));
        Assert.Null(route.SelectedAddress);
    }

    [Theory]
    [InlineData("[\"custom.example\"]", "custom.example")]
    [InlineData("{broken", "route-cn.disklens.site")]
    [InlineData("[]", "route-cn.disklens.site")]
    [InlineData("[\"https://invalid.example/path\"]", "route-cn.disklens.site")]
    public void Optional_configuration_file_is_bounded_and_invalid_content_keeps_defaults(string json, string first)
    {
        var directory = Path.Combine(Path.GetTempPath(), "DiskLens-route-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, SharedPreferredRouteDiscovery.SourcesConfigFileName);
        try
        {
            File.WriteAllText(file, json);
            Assert.Equal(first, SharedPreferredRouteDiscovery.ConfiguredSources(null, directory)[0]);
            Assert.Equal("environment.example", SharedPreferredRouteDiscovery.ConfiguredSources("environment.example", directory)[0]);
            File.WriteAllText(file, new string(' ', 8193) + "[\"custom.example\"]");
            Assert.Equal(SharedPreferredRouteDiscovery.DefaultSources, SharedPreferredRouteDiscovery.ConfiguredSources(null, directory));
        }
        finally { File.Delete(file); Directory.Delete(directory); }
    }

    private static SharedInitialConnectionRoute CreateRoute(Uri? endpoint = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null,
        Func<Uri, SharedSystemProxySnapshot>? capture = null,
        Func<IPAddress?, HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = null,
        IEnumerable<string>? candidateDnsSources = null)
        => new(endpoint ?? Official,
            resolve ?? ((host, _) => Task.FromResult(host == SharedPreferredRouteDiscovery.CandidateDnsHost
                ? new[] { Preferred, Alternate } : Array.Empty<IPAddress>())),
            capture ?? (_ => new(new WebProxy(), "direct", true)),
            (address, _) => new Handler((request, token) => (send ?? ((_, _, _) => Task.FromResult(Rules())))(address, request, token)),
            discoveryTimeout: TimeSpan.FromMilliseconds(40), initialTimeout: TimeSpan.FromMilliseconds(100),
            candidateRotation: 0, candidateDnsSources: candidateDnsSources ?? [SharedPreferredRouteDiscovery.CandidateDnsHost]);

    private static HttpResponseMessage Rules() => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"ruleVersion\":1,\"agreementVersion\":\"test\",\"rules\":[]}", Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Json(string text) => new(HttpStatusCode.OK)
        { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Registration() => Json("{\"installationId\":\"" + Guid.NewGuid()
        + "\",\"managementToken\":\"" + new string('t', 43) + "\",\"epoch\":0}");

    private static StreamContent BrokenBody()
    {
        var content = new StreamContent(new BrokenStream());
        content.Headers.ContentType = new("application/json");
        return content;
    }

    private sealed class BrokenStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new IOException("synthetic response-body disconnect"));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => Task.FromException<int>(new IOException("synthetic response-body disconnect"));
    }

    private sealed class StalledStream : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        { await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
    }

    private sealed class DeferredBrokenStream(TaskCompletionSource started, TaskCompletionSource release) : MemoryStream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            throw new IOException("synthetic old response-body failure");
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }

    private sealed class TrackingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        internal bool Disposed;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
        protected override void Dispose(bool disposing)
        {
            if (disposing) Disposed = true;
            base.Dispose(disposing);
        }
    }
}
