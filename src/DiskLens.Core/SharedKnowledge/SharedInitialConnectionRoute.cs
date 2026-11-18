using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DiskLens.Core.SharedKnowledge;

/// <summary>Selects a direct HTTPS route once per network, before business requests are sent.</summary>
internal sealed class SharedInitialConnectionRoute : HttpMessageHandler
{
    private const int MaximumProbeBytes = 1024 * 1024;
    private readonly Uri _endpoint;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _transportSync = new();
    private TransportLease _transport = new(new HttpMessageInvoker(new HttpClientHandler { AllowAutoRedirect = false }));
    private long _networkRevision;
    private long _initializedRevision = -1;
    private long _lastFailureRefresh;
    private string? _proxySignature;
    private bool _initialized;
    private bool _disposed;

    internal IPAddress? SelectedAddress { get; private set; }
    internal bool SwitchedAddress { get; private set; }

    internal SharedInitialConnectionRoute(Uri endpoint)
    {
        _endpoint = endpoint;
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => Interlocked.Increment(ref _networkRevision);
    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
        => Interlocked.Increment(ref _networkRevision);

    internal void RefreshAfterReadFailure(Exception exception)
    {
        if (exception is HttpRequestException { StatusCode: not null }) return;
        lock (_transportSync)
        {
            if (_disposed) return;
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
        var proxy = SharedSystemProxySnapshot.Capture(_endpoint);
        if (IsCurrent(proxy.Signature)) return;
        await _initializationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            proxy = SharedSystemProxySnapshot.Capture(_endpoint);
            if (IsCurrent(proxy.Signature)) return;
            var revision = Volatile.Read(ref _networkRevision);
            if (_endpoint.Scheme != Uri.UriSchemeHttps || _endpoint.IsLoopback || !proxy.IsDirect)
            {
                CompleteWithoutSelection(proxy, revision, cancellationToken);
                return;
            }

            // The first deadline covers DNS, TCP, TLS and the small complete reply.
            using var initialDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            initialDeadline.CancelAfter(TimeSpan.FromMilliseconds(5000));
            IPAddress[] addresses;
            try
            {
                addresses = (await Dns.GetHostAddressesAsync(_endpoint.DnsSafeHost, initialDeadline.Token).ConfigureAwait(false))
                    .Where(address => address.AddressFamily == AddressFamily.InterNetwork
                        || address.AddressFamily == AddressFamily.InterNetworkV6 && Socket.OSSupportsIPv6)
                    .Distinct().OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).ToArray();
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                // With no resolved alternate, keep the original resolver and timeouts.
                CompleteWithoutSelection(proxy, revision, cancellationToken);
                return;
            }
            if (addresses.Length < 2)
            {
                CompleteWithoutSelection(proxy, revision, cancellationToken);
                return;
            }

            var handler = CreatePinnedHandler(addresses[0], proxy.Proxy);
            HttpMessageInvoker? selected = new(handler);
            try
            {
                var switched = false;
                try
                {
                    using var probe = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
                    using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_endpoint, "v1/rules"));
                    request.Headers.UserAgent.ParseAdd("DiskLens/1.1.0");
                    using var response = await probe.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                        initialDeadline.Token).ConfigureAwait(false);
                    await ReadProbeAsync(response, initialDeadline.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (IsNetworkFailure(ex))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ThrowIfDisposed();
                    // Abort the first pool. Never replay an installation or upload POST.
                    selected.Dispose();
                    selected = new HttpMessageInvoker(CreatePinnedHandler(addresses[1], proxy.Proxy));
                    switched = true;
                }
                catch (InvalidDataException)
                {
                    // A bounded-format error says nothing about reachability of another IP.
                }
                cancellationToken.ThrowIfCancellationRequested();
                CommitSelection(selected, addresses[switched ? 1 : 0], switched, proxy.Signature, revision);
                selected = null;
            }
            finally { selected?.Dispose(); }
        }
        finally { _initializationGate.Release(); }
    }

    private bool IsCurrent(string proxySignature) => Volatile.Read(ref _initialized)
        && Volatile.Read(ref _initializedRevision) == Volatile.Read(ref _networkRevision)
        && string.Equals(_proxySignature, proxySignature, StringComparison.Ordinal);

    private static bool IsNetworkFailure(Exception exception) => exception is OperationCanceledException
        or SocketException or HttpRequestException or IOException;

    private static async Task ReadProbeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaximumProbeBytes)
            throw new InvalidDataException("共享经验连接探测响应过大。");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[8192];
        var total = 0;
        while (true)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) return;
            total += count;
            if (total > MaximumProbeBytes) throw new InvalidDataException("共享经验连接探测响应过大。");
        }
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
        string proxySignature, long revision)
    {
        HttpMessageInvoker? retired;
        lock (_transportSync)
        {
            ThrowIfDisposed();
            var previous = _transport;
            _transport = new(selected);
            SelectedAddress = address;
            SwitchedAddress = switched;
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
        HttpMessageInvoker? selected = new(new SocketsHttpHandler { AllowAutoRedirect = false, Proxy = proxy.Proxy });
        try
        {
            CommitSelection(selected, null, false, proxy.Signature, revision);
            selected = null;
        }
        finally { selected?.Dispose(); }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed)) throw new ObjectDisposedException(nameof(SharedInitialConnectionRoute));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        TransportLease transport;
        lock (_transportSync)
        {
            ThrowIfDisposed();
            transport = _transport;
            transport.Active++;
        }
        try
        {
            var response = await transport.Invoker.SendAsync(request, cancellationToken).ConfigureAwait(false);
            response.Content = new LeasedContent(response.Content, () => ReleaseTransport(transport));
            return response;
        }
        catch
        {
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

    private sealed class TransportLease(HttpMessageInvoker invoker)
    {
        internal HttpMessageInvoker Invoker { get; } = invoker;
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
