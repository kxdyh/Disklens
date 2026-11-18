using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace DiskLens.Core.Networking;

public interface INetworkAvailability
{
    bool IsNetworkAvailable { get; }
}

public sealed class SystemNetworkAvailability : INetworkAvailability
{
    public static SystemNetworkAvailability Instance { get; } = new();
    private readonly Func<bool> _isLinkAvailable;
    private readonly Func<bool?> _hasInternetAccess;
    private readonly Func<bool> _hasRoutableUplink;
    private SystemNetworkAvailability() : this(NetworkInterface.GetIsNetworkAvailable, GetInternetAvailability, HasRoutableUplink) { }
    internal SystemNetworkAvailability(Func<bool> isLinkAvailable, Func<bool?> hasInternetAccess,
        Func<bool>? hasRoutableUplink = null)
    {
        _isLinkAvailable = isLinkAvailable;
        _hasInternetAccess = hasInternetAccess;
        _hasRoutableUplink = hasRoutableUplink ?? (() => false);
    }
    public bool IsNetworkAvailable
    {
        get
        {
            // Windows' Internet hint can lag behind a VPN/proxy or link change.
            // An up interface with an actual gateway may still reach this service;
            // let its real HTTP request decide rather than vetoing it with NCSI.
            try { return _isLinkAvailable() && (_hasInternetAccess() != false || _hasRoutableUplink()); }
            catch (NetworkInformationException) { return true; }
        }
    }

    internal bool HasOfflineInternetHint
    {
        get
        {
            try { return _hasInternetAccess() == false; }
            catch (NetworkInformationException) { return false; }
        }
    }

    private static bool HasRoutableUplink()
        => NetworkInterface.GetAllNetworkInterfaces().Any(adapter =>
            adapter.OperationalStatus == OperationalStatus.Up
            && adapter.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            && adapter.GetIPProperties().GatewayAddresses.Any(gateway =>
                !gateway.Address.Equals(IPAddress.Any) && !gateway.Address.Equals(IPAddress.IPv6Any)));

    internal bool IsAvailableForEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) return IsNetworkAvailable;
        var host = uri.IdnHost.Trim('[', ']');
        var local = host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || !host.Contains('.') && !host.Contains(':');
        if (IPAddress.TryParse(host, out var address))
        {
            if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            var bytes = address.GetAddressBytes();
            local = address.AddressFamily == AddressFamily.InterNetwork
                ? bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
                    || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 169 && bytes[1] == 254
                : address.IsIPv6LinkLocal || (bytes[0] & 0xfe) == 0xfc;
        }
        if (!local) return IsNetworkAvailable;
        // A LAN-hosted model does not need public Internet connectivity.
        try { return _isLinkAvailable(); }
        catch (NetworkInformationException) { return true; }
    }

    private static bool? GetInternetAvailability()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            // An active host-only/VPN adapter can keep GetIsNetworkAvailable true
            // after Wi-Fi/Ethernet disconnects. Use Windows' aggregate Internet
            // connectivity hint; unknown/unsupported states remain inconclusive.
            if (GetNetworkConnectivityHint(out var hint) != 0) return null;
            return hint.ConnectivityLevel switch
            {
                1 or 2 => false, // None / LocalAccess
                3 or 4 => true,  // InternetAccess / ConstrainedInternetAccess
                _ => null,
            };
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ConnectivityHint
    {
        public uint ConnectivityLevel;
        public uint ConnectivityCost;
        public byte ApproachingDataLimit;
        public byte OverDataLimit;
        public byte Roaming;
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetNetworkConnectivityHint(out ConnectivityHint hint);
}

public static class NetworkFailureClassifier
{
    public const string OfflineMessage = "未联网，请联网后重试";

    // An invalid endpoint remains a configuration error, not an offline error.
    public static bool RequiresNetwork(string endpoint) => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && !uri.IsLoopback;

    public static bool IsNetworkUnavailable(string endpoint, INetworkAvailability? availability = null)
    {
        var network = availability ?? SystemNetworkAvailability.Instance;
        return RequiresNetwork(endpoint) && !(network is SystemNetworkAvailability system
            ? system.IsAvailableForEndpoint(endpoint) : network.IsNetworkAvailable);
    }

    public static bool IsOffline(Exception exception, INetworkAvailability? availability = null, string? endpoint = null)
    {
        var chain = new List<Exception>();
        for (Exception? current = exception; current is not null; current = current.InnerException) chain.Add(current);
        // A server response proves the request reached a service. A timeout by
        // itself is inconclusive; HttpClient wraps its timeout in cancellation.
        if (chain.Any(error => error is HttpRequestException { StatusCode: not null })) return false;
        if (chain.Any(error => error is OperationCanceledException)
            && !chain.Any(error => error is TimeoutException)) return false;
        if (chain.Any(error => error is NetworkUnavailableException)) return true;
        if (chain.OfType<SocketException>().Any(error => error.SocketErrorCode is SocketError.NetworkDown or SocketError.NetworkUnreachable))
            return true;
        var network = availability ?? SystemNetworkAvailability.Instance;
        return chain.Any(error => error is HttpRequestException or SocketException or TimeoutException)
            && (endpoint is null ? !network.IsNetworkAvailable
                    || network is SystemNetworkAvailability { HasOfflineInternetHint: true }
                : IsNetworkUnavailable(endpoint, network));
    }
}

public sealed class NetworkUnavailableException(Exception? innerException = null)
    : HttpRequestException(NetworkFailureClassifier.OfflineMessage, innerException);
