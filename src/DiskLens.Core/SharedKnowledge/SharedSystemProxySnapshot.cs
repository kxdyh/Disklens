using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace DiskLens.Core.SharedKnowledge;

/// <summary>Refreshes this service's manual Windows proxy without changing process or OS settings.</summary>
internal sealed record SharedSystemProxySnapshot(IWebProxy Proxy, string Signature, bool IsDirect)
{
    internal static SharedSystemProxySnapshot Capture(Uri endpoint)
    {
        var current = HttpClient.DefaultProxy;
        var identity = RuntimeHelpers.GetHashCode(current).ToString(System.Globalization.CultureInfo.InvariantCulture);
        // Keep explicit application/environment proxy choices, including their
        // authentication and bypass rules. Only the runtime's Windows snapshot
        // needs refreshing: net8 caches its manual settings for the process.
        if (OperatingSystem.IsWindows() && current.GetType().Assembly == typeof(HttpClient).Assembly
            && !HasEnvironmentProxy() && TryReadWindows(out var settings))
        {
            var signature = identity + "|" + settings.Signature;
            if (!settings.AutoDetect && string.IsNullOrWhiteSpace(settings.AutoConfigUrl))
            {
                var selected = SelectManualProxy(settings.ProxyServer, endpoint);
                if (selected is null || IsBypassed(settings.ProxyBypass, endpoint))
                    return new(new WebProxy(), signature, true);
                if (Uri.TryCreate(selected.Contains("://", StringComparison.Ordinal) ? selected : "http://" + selected,
                    UriKind.Absolute, out var proxyUri))
                    return new(new WebProxy(proxyUri) { Credentials = current.Credentials }, signature, false);
                // A malformed configuration must not cause an implicit bypass.
            }
            return FromProxy(current, endpoint, signature);
        }
        return FromProxy(current, endpoint, identity);
    }

    private static SharedSystemProxySnapshot FromProxy(IWebProxy proxy, Uri endpoint, string signature)
    {
        try
        {
            var destination = proxy.IsBypassed(endpoint) ? null : proxy.GetProxy(endpoint);
            var direct = destination is null || destination == endpoint;
            return new(proxy, signature + "|" + (direct ? "direct" : destination!.AbsoluteUri), direct);
        }
        catch
        {
            // PAC/authentication failures remain with the configured proxy.
            return new(proxy, signature + "|unresolved", false);
        }
    }

    private static bool HasEnvironmentProxy() => new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY" }
        .Any(name => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)));

    private static string? SelectManualProxy(string? value, Uri endpoint)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string? generic = null;
        foreach (var part in value.Split([';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = part.IndexOf('=');
            if (separator < 0) generic ??= part;
            else if (part.AsSpan(0, separator).Equals(endpoint.Scheme, StringComparison.OrdinalIgnoreCase))
                return part[(separator + 1)..];
        }
        return generic;
    }

    private static bool IsBypassed(string? bypass, Uri endpoint)
    {
        if (endpoint.IsLoopback) return true;
        foreach (var part in (bypass ?? "").Split([';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Equals("<local>", StringComparison.OrdinalIgnoreCase))
            {
                if (!endpoint.Host.Contains('.')) return true;
                continue;
            }
            var target = part.Contains("://", StringComparison.Ordinal) ? endpoint.GetLeftPart(UriPartial.Authority)
                : part.Contains(':') ? endpoint.Authority : endpoint.Host;
            var pattern = "\\A" + Regex.Escape(part).Replace("\\*", ".*") + "\\z";
            if (Regex.IsMatch(target, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(50))) return true;
        }
        return false;
    }

    private sealed record WindowsProxySettings(bool AutoDetect, string? AutoConfigUrl, string? ProxyServer, string? ProxyBypass)
    {
        internal string Signature => $"{AutoDetect}|{AutoConfigUrl}|{ProxyServer}|{ProxyBypass}";
    }

    private static bool TryReadWindows(out WindowsProxySettings settings)
    {
        settings = new(false, null, null, null);
        NativeProxyConfig config = default;
        try
        {
            if (!WinHttpGetIEProxyConfigForCurrentUser(out config)) return false;
            settings = new(config.AutoDetect, Marshal.PtrToStringUni(config.AutoConfigUrl),
                Marshal.PtrToStringUni(config.Proxy), Marshal.PtrToStringUni(config.ProxyBypass));
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
        finally
        {
            if (config.AutoConfigUrl != IntPtr.Zero) GlobalFree(config.AutoConfigUrl);
            if (config.Proxy != IntPtr.Zero) GlobalFree(config.Proxy);
            if (config.ProxyBypass != IntPtr.Zero) GlobalFree(config.ProxyBypass);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeProxyConfig
    {
        [MarshalAs(UnmanagedType.Bool)] internal bool AutoDetect;
        internal IntPtr AutoConfigUrl;
        internal IntPtr Proxy;
        internal IntPtr ProxyBypass;
    }

    [DllImport("winhttp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WinHttpGetIEProxyConfigForCurrentUser(out NativeProxyConfig config);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);
}
