using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace DiskLens.Core.SharedKnowledge;

/// <summary>Selects a direct HTTPS route once per network, before business requests are sent.</summary>
internal sealed class SharedInitialConnectionRoute : HttpMessageHandler
{
    private const int MaximumProbeBytes = 1024 * 1024;
    private static readonly HttpRequestOptionsKey<TransportLease> TransportKey = new("DiskLens.SharedRoute.Transport");
    internal static readonly HttpRequestOptionsKey<SharedRouteRequestSnapshot> DiagnosticKey = new("DiskLens.SharedRoute.Diagnostic");
    private readonly Uri _endpoint;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;
    private readonly Func<Uri, SharedSystemProxySnapshot> _captureProxy;
    private readonly Func<IPAddress?, IWebProxy, HttpMessageHandler> _createHandler;
    private readonly TimeSpan _discoveryTimeout;
    private readonly TimeSpan _initialTimeout;
    private readonly int _candidateRotation;
    private readonly string[] _candidateDnsSources;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _transportSync = new();
    private TransportLease _transport = new(new HttpMessageInvoker(new HttpClientHandler { AllowAutoRedirect = false }));
    private long _networkRevision;
    private long _initializedRevision = -1;
    private long _lastFailureRefresh;
    private long _preferredSuppressedRevision = -1;
    private string? _preferredSuppressedSignature;
    private string? _proxySignature;
    private bool _initialized;
    private bool _disposed;

    internal IPAddress? SelectedAddress { get; private set; }
    internal bool SwitchedAddress { get; private set; }
    internal string SelectedSource { get; private set; } = "origin-resolver";
    internal string? SelectedDnsHost { get; private set; }
    internal IReadOnlyList<string> CandidateDnsSources => Array.AsReadOnly(_candidateDnsSources);
    internal IReadOnlyList<SharedRouteDnsSourceDiagnostic> DiscoveredSources { get; private set; }
        = Array.Empty<SharedRouteDnsSourceDiagnostic>();
    internal const string CandidateDnsHost = SharedPreferredRouteDiscovery.CandidateDnsHost;

    internal SharedInitialConnectionRoute(Uri endpoint,
        Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null,
        Func<Uri, SharedSystemProxySnapshot>? captureProxy = null,
        Func<IPAddress?, IWebProxy, HttpMessageHandler>? createHandler = null,
        TimeSpan? discoveryTimeout = null, TimeSpan? initialTimeout = null, int candidateRotation = -1,
        IEnumerable<string>? candidateDnsSources = null)
    {
        _endpoint = endpoint;
        _resolve = resolve ?? ((host, token) => Dns.GetHostAddressesAsync(host, token));
        _captureProxy = captureProxy ?? SharedSystemProxySnapshot.Capture;
        _createHandler = createHandler ?? ((address, proxy) => address is null
            ? new SocketsHttpHandler { AllowAutoRedirect = false, Proxy = proxy }
            : CreatePinnedHandler(address, proxy));
        _discoveryTimeout = discoveryTimeout ?? SharedPreferredRouteDiscovery.DiscoveryTimeout;
        _initialTimeout = initialTimeout ?? TimeSpan.FromMilliseconds(5000);
        _candidateRotation = candidateRotation;
        _candidateDnsSources = candidateDnsSources is null ? SharedPreferredRouteDiscovery.ConfiguredSources()
            : SharedPreferredRouteDiscovery.NormalizeSources(candidateDnsSources);
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => InvalidateForNetworkChange();
    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
        => InvalidateForNetworkChange();

    internal void InvalidateForNetworkChange() => Interlocked.Increment(ref _networkRevision);

    internal void NotifyResponseFailure(HttpRequestMessage? request, Exception exception)
    {
        if (request is null || !request.Options.TryGetValue(TransportKey, out var failed)) return;
        if (failed.Preferred)
        {
            FallBackToOrigin(failed);
            return;
        }
        if (exception is InvalidDataException or JsonException) return;
        if (exception is HttpRequestException { StatusCode: not null }) return;
        lock (_transportSync)
        {
            if (_disposed || !ReferenceEquals(_transport, failed)) return;
            // The existing read retry must use the origin we just selected, not rediscover the failed edge.
            if (PreferredIsSuppressed(_proxySignature, Volatile.Read(ref _networkRevision))) return;
            var now = Environment.TickCount64;
            if (now - _lastFailureRefresh < 2000) return;
            _lastFailureRefresh = now;
            Interlocked.Increment(ref _networkRevision);
        }
    }

    internal async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        var proxy = _captureProxy(_endpoint);
        if (IsCurrent(proxy.Signature)) return;
        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            proxy = _captureProxy(_endpoint);
            if (IsCurrent(proxy.Signature)) return;
            var revision = Volatile.Read(ref _networkRevision);
            if (_endpoint.Scheme != Uri.UriSchemeHttps || _endpoint.IsLoopback || !proxy.IsDirect)
            {
                CompleteWithoutSelection(proxy, revision, cancellationToken);
                return;
            }

            // Discovery and the first probe share one deadline, not 750 ms plus another five seconds.
            using var initialDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            initialDeadline.CancelAfter(_initialTimeout);
            try
            {
                // Begin origin resolution concurrently. Candidate DNS failures cannot hold up this fallback.
                var originTask = ResolveOrEmptyAsync(_endpoint.DnsSafeHost, initialDeadline.Token);
                var officialDirect = SharedPreferredRouteDiscovery.IsOfficialOrigin(_endpoint);
                var preferred = officialDirect
                    && !PreferredIsSuppressed(proxy.Signature, revision)
                    ? await DiscoverPreferredAsync(initialDeadline.Token).ConfigureAwait(false) : [];
                cancellationToken.ThrowIfCancellationRequested();
                ThrowIfDisposed();
                if (officialDirect && !PreferredIsSuppressed(proxy.Signature, revision))
                {
                    await SelectRacedRouteAsync(preferred, originTask, proxy, revision,
                        initialDeadline.Token, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var addresses = (await originTask.ConfigureAwait(false))
                    .Where(address => address.AddressFamily == AddressFamily.InterNetwork
                        || address.AddressFamily == AddressFamily.InterNetworkV6 && Socket.OSSupportsIPv6)
                    .Where(address => !SharedPreferredRouteDiscovery.IsOfficialOrigin(_endpoint)
                        || SharedPreferredRouteDiscovery.IsCloudflareEdge(address))
                    .Distinct().OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ToArray();
                cancellationToken.ThrowIfCancellationRequested();
                if (addresses.Length < 2)
                {
                    CompleteWithoutSelection(proxy, revision, cancellationToken);
                    return;
                }

                var handler = _createHandler(addresses[0], proxy.Proxy);
                HttpMessageInvoker? selected = new(handler);
                try
                {
                    var switched = false;
                    try { await ProbeAsync(handler, initialDeadline.Token, validateRules: false).ConfigureAwait(false); }
                    catch (Exception ex) when (IsNetworkFailure(ex))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ThrowIfDisposed();
                        selected.Dispose();
                        selected = new HttpMessageInvoker(_createHandler(addresses[1], proxy.Proxy));
                        switched = true;
                    }
                    catch (InvalidDataException) { /* Preserve the existing custom/origin route behavior. */ }
                    cancellationToken.ThrowIfCancellationRequested();
                    CommitSelection(selected, addresses[switched ? 1 : 0], switched, proxy.Signature, revision,
                        "origin-dns");
                    selected = null;
                }
                finally { selected?.Dispose(); }
            }
            finally
            {
                // Stop a still-running origin lookup when a preferred route was selected.
                await initialDeadline.CancelAsync().ConfigureAwait(false);
            }
        }
        finally { _initializationGate.Release(); }
    }

    private bool IsCurrent(string proxySignature) => Volatile.Read(ref _initialized)
        && Volatile.Read(ref _initializedRevision) == Volatile.Read(ref _networkRevision)
        && string.Equals(_proxySignature, proxySignature, StringComparison.Ordinal);

    private bool PreferredIsSuppressed(string? signature, long revision)
        => Volatile.Read(ref _preferredSuppressedRevision) == revision
            && string.Equals(_preferredSuppressedSignature, signature, StringComparison.Ordinal);

    private async Task<IPAddress[]> ResolveOrEmptyAsync(string host, CancellationToken cancellationToken)
    {
        try { return await _resolve(host, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        { return []; }
    }

    private async Task<SharedPreferredRouteCandidate[]> DiscoverPreferredAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_discoveryTimeout);
        var sources = await Task.WhenAll(_candidateDnsSources.Select(async host =>
        {
            IPAddress[] addresses = [];
            var outcome = "ok";
            try { addresses = await _resolve(host, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { outcome = "timeout"; }
            catch (Exception ex) when (ex is SocketException or ArgumentException) { outcome = "dns-failure"; }
            var accepted = addresses.Where(SharedPreferredRouteDiscovery.IsCloudflareEdge).Distinct().ToArray();
            if (outcome == "ok" && accepted.Length == 0) outcome = addresses.Length == 0 ? "empty" : "no-cloudflare-address";
            return (Host: host, Addresses: accepted, Diagnostic:
                new SharedRouteDnsSourceDiagnostic(host, addresses.Length, accepted.Length, outcome));
        })).ConfigureAwait(false);
        DiscoveredSources = sources.Select(source => source.Diagnostic).ToArray();
        return SharedPreferredRouteDiscovery.SelectProbeSources(
            sources.Select(source => (source.Host, source.Addresses)), _candidateRotation);
    }

    private async Task SelectRacedRouteAsync(SharedPreferredRouteCandidate[] preferred,
        Task<IPAddress[]> originTask, SharedSystemProxySnapshot proxy, long revision,
        CancellationToken initialDeadline, CancellationToken cancellationToken)
    {
        using var raceCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            initialDeadline, cancellationToken, _lifetime.Token);
        var attempts = new List<ProbeAttempt>();
        var attemptsSync = new object();
        var probeTasks = new List<Task<ProbeOutcome?>>();
        var usedAddresses = new HashSet<IPAddress>(preferred.Select(candidate => candidate.Address));

        foreach (var candidate in preferred)
            probeTasks.Add(StartProbe(candidate, isPreferred: true, proxy, raceCancellation.Token, attempts, attemptsSync));
        // Origin resolution runs alongside preferred probes.  At most two origin
        // addresses enter this six-probe race and they remain diagnostic only.
        probeTasks.Add(ProbeOriginWhenReadyAsync(originTask, proxy, raceCancellation.Token,
            attempts, attemptsSync, usedAddresses));

        ProbeOutcome? winner = null;
        try
        {
            while (probeTasks.Count > 0)
            {
                var completed = await Task.WhenAny(probeTasks).ConfigureAwait(false);
                probeTasks.Remove(completed);
                var result = await completed.ConfigureAwait(false);
                if (result is not null)
                {
                    winner = result;
                    break;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            ThrowIfDisposed();
            if (winner is null)
            {
                // The shared five-second budget expired (or every read-only probe
                // failed).  Business requests now use the normal resolver timeout.
                SuppressPreferred(proxy.Signature, revision);
                raceCancellation.Cancel();
                DisposeProbeAttempts(attempts, attemptsSync, null);
                CompleteWithoutSelection(proxy, revision, cancellationToken);
                return;
            }

            HttpMessageInvoker? selected = winner.Attempt.TakeInvoker();
            try
            {
                raceCancellation.Cancel();
                DisposeProbeAttempts(attempts, attemptsSync, winner.Attempt);
                cancellationToken.ThrowIfCancellationRequested();
                CommitSelection(selected, winner.Candidate.Address, switched: false,
                    proxy.Signature, revision, winner.IsPreferred ? "preferred-dns" : "origin-dns",
                    winner.IsPreferred ? winner.Candidate.Host : null);
                selected = null;
            }
            finally { selected?.Dispose(); }
        }
        finally
        {
            raceCancellation.Cancel();
            // A successful invoker has transferred ownership, so this also closes
            // every pool if caller cancellation races with the winning reply.
            DisposeProbeAttempts(attempts, attemptsSync, null);
        }
    }

    private Task<ProbeOutcome?> StartProbe(SharedPreferredRouteCandidate candidate, bool isPreferred,
        SharedSystemProxySnapshot proxy, CancellationToken cancellationToken,
        List<ProbeAttempt> attempts, object attemptsSync)
    {
        var attempt = new ProbeAttempt(candidate, new HttpMessageInvoker(
            _createHandler(candidate.Address, proxy.Proxy)), isPreferred);
        lock (attemptsSync) attempts.Add(attempt);
        return ProbeCandidateAsync(attempt, cancellationToken);
    }

    private async Task<ProbeOutcome?> ProbeCandidateAsync(ProbeAttempt attempt, CancellationToken cancellationToken)
    {
        try
        {
            await ProbeAsync(attempt.Invoker, cancellationToken, validateRules: true)
                .WaitAsync(cancellationToken).ConfigureAwait(false);
            return new ProbeOutcome(attempt, attempt.Candidate);
        }
        catch
        {
            attempt.Dispose();
            return null;
        }
    }

    private async Task<ProbeOutcome?> ProbeOriginWhenReadyAsync(Task<IPAddress[]> originTask,
        SharedSystemProxySnapshot proxy, CancellationToken cancellationToken,
        List<ProbeAttempt> attempts, object attemptsSync, HashSet<IPAddress> usedAddresses)
    {
        try
        {
            var addresses = (await originTask.WaitAsync(cancellationToken).ConfigureAwait(false))
                .Where(address => (address.AddressFamily == AddressFamily.InterNetwork
                    || address.AddressFamily == AddressFamily.InterNetworkV6 && Socket.OSSupportsIPv6)
                    && SharedPreferredRouteDiscovery.IsCloudflareEdge(address))
                .Distinct().Where(usedAddresses.Add)
                .OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                .Take(2).ToArray();
            var originProbes = addresses.Select(address => StartProbe(
                new SharedPreferredRouteCandidate(address, _endpoint.DnsSafeHost), isPreferred: false, proxy,
                cancellationToken, attempts, attemptsSync)).ToList();
            while (originProbes.Count > 0)
            {
                var completed = await Task.WhenAny(originProbes).ConfigureAwait(false);
                originProbes.Remove(completed);
                var result = await completed.ConfigureAwait(false);
                if (result is not null) return result;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        return null;
    }

    private static void DisposeProbeAttempts(List<ProbeAttempt> attempts, object attemptsSync,
        ProbeAttempt? keep)
    {
        ProbeAttempt[] snapshot;
        lock (attemptsSync) snapshot = attempts.ToArray();
        foreach (var attempt in snapshot)
            if (!ReferenceEquals(attempt, keep)) attempt.Dispose();
    }

    private async Task ProbeAsync(HttpMessageHandler handler, CancellationToken cancellationToken, bool validateRules)
    {
        using var probe = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_endpoint, "v1/rules"));
        request.Headers.UserAgent.ParseAdd("DiskLens/1.1.0");
        using var response = await probe.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        await ReadProbeAsync(response, cancellationToken, validateRules).ConfigureAwait(false);
    }

    private async Task ProbeAsync(HttpMessageInvoker invoker, CancellationToken cancellationToken, bool validateRules)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_endpoint, "v1/rules"));
        request.Headers.UserAgent.ParseAdd("DiskLens/1.1.0");
        using var response = await invoker.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await ReadProbeAsync(response, cancellationToken, validateRules).ConfigureAwait(false);
    }

    private static bool IsNetworkFailure(Exception exception) => exception is OperationCanceledException
        or SocketException or HttpRequestException or IOException;

    private static async Task ReadProbeAsync(HttpResponseMessage response, CancellationToken cancellationToken, bool validateRules)
    {
        if (validateRules && (response.StatusCode != HttpStatusCode.OK
            || !string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("共享经验连接探测未返回规则数据。");
        if (response.Content.Headers.ContentLength > MaximumProbeBytes)
            throw new InvalidDataException("共享经验连接探测响应过大。");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[8192];
        var total = 0;
        using var body = validateRules ? new MemoryStream() : null;
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            total += count;
            if (total > MaximumProbeBytes) throw new InvalidDataException("共享经验连接探测响应过大。");
            body?.Write(buffer, 0, count);
        }
        if (body is null) return;
        try
        {
            using var json = JsonDocument.Parse(body.ToArray());
            if (json.RootElement.ValueKind != JsonValueKind.Object
                || !json.RootElement.TryGetProperty("ruleVersion", out var version)
                || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var ruleVersion) || ruleVersion <= 0
                || !json.RootElement.TryGetProperty("agreementVersion", out var agreement)
                || agreement.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(agreement.GetString())
                || !json.RootElement.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array
                || rules.EnumerateArray().Any(rule => rule.ValueKind != JsonValueKind.Object))
                throw new InvalidDataException("共享经验连接探测未返回规则数据。");
        }
        catch (JsonException ex) { throw new InvalidDataException("共享经验连接探测返回格式无效。", ex); }
    }

    private SocketsHttpHandler CreatePinnedHandler(IPAddress address, IWebProxy proxy) => new()
    {
        AllowAutoRedirect = false,
        Proxy = proxy,
        // Proxies retain their actual destination; TLS always retains the URL host.
        ConnectCallback = async (context, cancellationToken) =>
        {
            var isOrigin = context.DnsEndPoint.Port == _endpoint.Port
                && (string.Equals(context.DnsEndPoint.Host, _endpoint.IdnHost, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(context.DnsEndPoint.Host, _endpoint.DnsSafeHost, StringComparison.OrdinalIgnoreCase));
            var socket = isOrigin ? new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                : new Socket(SocketType.Stream, ProtocolType.Tcp);
            socket.NoDelay = true;
            try
            {
                if (isOrigin)
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                else
                    await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
                // The HTTP handler still performs TLS against the original URL host.
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch { socket.Dispose(); throw; }
        }
    };

    private void CommitSelection(HttpMessageInvoker selected, IPAddress? address, bool switched,
        string proxySignature, long revision, string source, string? dnsHost = null)
    {
        HttpMessageInvoker? retired;
        lock (_transportSync)
        {
            ThrowIfDisposed();
            var previous = _transport;
            _transport = new(selected)
            {
                Preferred = source == "preferred-dns", Revision = revision, ProxySignature = proxySignature,
                Snapshot = new(source, address is not null && SharedPreferredRouteDiscovery.IsCloudflareEdge(address)
                    ? address.ToString() : null, dnsHost)
            };
            SelectedAddress = address;
            SwitchedAddress = switched;
            SelectedSource = source;
            SelectedDnsHost = dnsHost;
            _proxySignature = proxySignature;
            Volatile.Write(ref _initializedRevision, revision);
            Volatile.Write(ref _initialized, true);
            previous.Retired = true;
            retired = previous.Active == 0 ? previous.Invoker : null;
        }
        retired?.Dispose();
    }

    private void CompleteWithoutSelection(SharedSystemProxySnapshot proxy, long revision, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        HttpMessageInvoker? selected = new(_createHandler(null, proxy.Proxy));
        try
        {
            CommitSelection(selected, null, false, proxy.Signature, revision, proxy.IsDirect ? "origin-resolver" : "proxy");
            selected = null;
        }
        finally { selected?.Dispose(); }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed)) throw new ObjectDisposedException(nameof(SharedInitialConnectionRoute));
    }

    private void SuppressPreferred(string signature, long revision)
    {
        _preferredSuppressedSignature = signature;
        Volatile.Write(ref _preferredSuppressedRevision, revision);
    }

    private void FallBackToOrigin(TransportLease failed)
    {
        var proxy = _captureProxy(_endpoint);
        HttpMessageInvoker? replacement = new(_createHandler(null, proxy.Proxy));
        HttpMessageInvoker? retired = null;
        try
        {
            lock (_transportSync)
            {
                if (_disposed || !ReferenceEquals(_transport, failed) || !failed.Preferred) return;
                // Mark only this network/proxy combination. A real network change may discover candidates again.
                SuppressPreferred(failed.ProxySignature, failed.Revision);
                _transport = new(replacement)
                {
                    Revision = failed.Revision, ProxySignature = proxy.Signature,
                    Snapshot = new(proxy.IsDirect ? "origin-resolver" : "proxy", null, null)
                };
                SelectedAddress = null;
                SelectedDnsHost = null;
                SelectedSource = proxy.IsDirect ? "origin-resolver" : "proxy";
                SwitchedAddress = true;
                _proxySignature = proxy.Signature;
                // A concurrent network change keeps this selection stale and is handled by EnsureInitializedAsync.
                Volatile.Write(ref _initializedRevision, failed.Revision);
                failed.Retired = true;
                retired = failed.Active == 0 ? failed.Invoker : null;
                replacement = null;
            }
        }
        finally { replacement?.Dispose(); retired?.Dispose(); }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        TransportLease transport;
        lock (_transportSync)
        {
            ThrowIfDisposed();
            transport = _transport;
            transport.Active++;
            request.Options.Set(TransportKey, transport);
            request.Options.Set(DiagnosticKey, transport.Snapshot);
        }
        try
        {
            var response = await transport.Invoker.SendAsync(request, cancellationToken).ConfigureAwait(false);
            // Let the client classify HTTP failures before demoting an edge: the operator's
            // new-contributor pause is an intentional 503 business response, not a broken route.
            response.Content = new LeasedContent(response.Content, () => ReleaseTransport(transport));
            return response;
        }
        catch (Exception ex)
        {
            // The client can distinguish a business deadline from caller cancellation; do not
            // demote a route merely because the user stopped the operation.
            if (transport.Preferred && IsNetworkFailure(ex) && ex is not OperationCanceledException)
                FallBackToOrigin(transport);
            ReleaseTransport(transport);
            throw;
        }
    }

    private void ReleaseTransport(TransportLease transport)
    {
        HttpMessageInvoker? retired;
        lock (_transportSync)
        {
            transport.Active--;
            retired = transport.Retired && transport.Active == 0 ? transport.Invoker : null;
        }
        retired?.Dispose();
    }

    private sealed class ProbeAttempt(SharedPreferredRouteCandidate candidate, HttpMessageInvoker invoker,
        bool isPreferred)
        : IDisposable
    {
        private int _ownership;
        internal SharedPreferredRouteCandidate Candidate { get; } = candidate;
        internal HttpMessageInvoker Invoker { get; } = invoker;
        internal bool IsPreferred { get; } = isPreferred;

        internal HttpMessageInvoker TakeInvoker()
        {
            if (Interlocked.Exchange(ref _ownership, 1) != 0)
                throw new InvalidOperationException("探测连接已被释放。");
            return Invoker;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ownership, 1) == 0) Invoker.Dispose();
        }
    }

    private sealed record ProbeOutcome(ProbeAttempt Attempt, SharedPreferredRouteCandidate Candidate)
    {
        internal bool IsPreferred => Attempt.IsPreferred;
    }

    private sealed class TransportLease(HttpMessageInvoker invoker)
    {
        internal HttpMessageInvoker Invoker { get; } = invoker;
        internal bool Preferred;
        internal long Revision;
        internal string ProxySignature = "";
        internal SharedRouteRequestSnapshot Snapshot = new("origin-resolver", null, null);
        internal int Active;
        internal bool Retired;
    }

    private sealed class LeasedContent : HttpContent
    {
        private readonly HttpContent _inner;
        private Action? _release;
        internal LeasedContent(HttpContent inner, Action release)
        {
            _inner = inner;
            _release = release;
            foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => _inner.CopyToAsync(stream);
        protected override Task<Stream> CreateContentReadStreamAsync() => _inner.ReadAsStreamAsync();
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
            => _inner.ReadAsStreamAsync(cancellationToken);
        protected override bool TryComputeLength(out long length)
        {
            length = _inner.Headers.ContentLength ?? 0;
            return _inner.Headers.ContentLength.HasValue;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _inner.Dispose(); }
                finally { Interlocked.Exchange(ref _release, null)?.Invoke(); }
            }
            base.Dispose(disposing);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            HttpMessageInvoker? retired = null;
            var unsubscribe = false;
            lock (_transportSync)
            {
                if (!_disposed)
                {
                    Volatile.Write(ref _disposed, true);
                    unsubscribe = true;
                    _transport.Retired = true;
                    if (_transport.Active == 0) retired = _transport.Invoker;
                }
            }
            if (unsubscribe)
            {
                NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
                NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
                _lifetime.Cancel();
                retired?.Dispose();
                _lifetime.Dispose();
                // The gate may still have cancellation waiters/finally releases.
            }
        }
        base.Dispose(disposing);
    }
}

internal sealed record SharedRouteRequestSnapshot(string Source, string? Address, string? DnsHost);
