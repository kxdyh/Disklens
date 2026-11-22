using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace DiskLens.Core.SharedKnowledge;

/// <summary>Discovers operator-managed edge candidates without changing the HTTPS origin.</summary>
internal static class SharedPreferredRouteDiscovery
{
    internal const string CandidateDnsHost = "route-cn.disklens.site";
    internal const string SourcesEnvironmentVariable = "DISKLENS_ROUTE_DNS_SOURCES";
    internal const string SourcesConfigFileName = "cloud-route-sources.json";
    internal static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromMilliseconds(750);
    internal static readonly string[] DefaultSources = [CandidateDnsHost, "icook.tw", "cloudflare-ip.mofashi.ltd", "nrt.xxxxxxxx.nyc.mn"];

    internal static string[] ConfiguredSources() => ConfiguredSources(
        Environment.GetEnvironmentVariable(SourcesEnvironmentVariable), AppContext.BaseDirectory);

    internal static string[] ConfiguredSources(string? configured, string baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var environmentSources = NormalizeSources(configured.Split([';', ',', ' ', '\r', '\n', '\t'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            return environmentSources.Length > 0 ? environmentSources : DefaultSources.ToArray();
        }
        try
        {
            using var file = File.OpenRead(Path.Combine(baseDirectory, SourcesConfigFileName));
            // Bound the read itself, including a file changed between opening and parsing.
            var buffer = new byte[8193];
            var length = 0;
            while (length < buffer.Length)
            {
                var read = file.Read(buffer, length, buffer.Length - length);
                if (read == 0) break;
                length += read;
            }
            if (length > 8192) return DefaultSources.ToArray();
            using var json = JsonDocument.Parse(buffer.AsMemory(0, length));
            if (json.RootElement.ValueKind == JsonValueKind.Array)
            {
                var sources = NormalizeSources(json.RootElement.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!));
                if (sources.Length > 0) return sources;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { /* An optional operator configuration cannot disable the default service. */ }
        return DefaultSources.ToArray();
    }

    internal static string[] NormalizeSources(IEnumerable<string> sources) => sources
        .Select(source => source.Trim().TrimEnd('.').ToLowerInvariant())
        .Where(source => source.Length is > 0 and <= 253 && source.Contains('.')
            && Uri.CheckHostName(source) == UriHostNameType.Dns)
        .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();

    // Cloudflare's published edge ranges, checked 2026-11-19:
    // https://www.cloudflare.com/ips-v4 and https://www.cloudflare.com/ips-v6
    // A DNS candidate is not evidence of domestic reachability; HTTPS still verifies the API host.
    private static readonly (byte[] Prefix, int Bits)[] EdgeRanges = new[]
    {
        "173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "103.31.4.0/22",
        "141.101.64.0/18", "108.162.192.0/18", "190.93.240.0/20", "188.114.96.0/20",
        "197.234.240.0/22", "198.41.128.0/17", "162.158.0.0/15", "104.16.0.0/13",
        "104.24.0.0/14", "172.64.0.0/13", "131.0.72.0/22",
        "2400:cb00::/32", "2606:4700::/32", "2803:f800::/32", "2405:b500::/32",
        "2405:8100::/32", "2a06:98c0::/29", "2c0f:f248::/32"
    }.Select(range =>
    {
        var parts = range.Split('/');
        return (IPAddress.Parse(parts[0]).GetAddressBytes(), int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
    }).ToArray();

    internal static bool IsOfficialOrigin(Uri endpoint) => endpoint.Scheme == Uri.UriSchemeHttps
        && endpoint.IsDefaultPort && endpoint.AbsolutePath == "/"
        && endpoint.Host.Equals("api.disklens.site", StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrEmpty(endpoint.Query) && string.IsNullOrEmpty(endpoint.Fragment)
        && string.IsNullOrEmpty(endpoint.UserInfo);

    internal static IPAddress[] SelectCandidates(IEnumerable<IPAddress> addresses, int rotation = -1)
    {
        var eligible = addresses.Where(address => (address.AddressFamily == AddressFamily.InterNetwork
            || address.AddressFamily == AddressFamily.InterNetworkV6 && Socket.OSSupportsIPv6)
            && IsCloudflareEdge(address))
            .Distinct().ToArray();
        // Rotate within each family so adding an A record does not leave it permanently unused.
        return eligible.GroupBy(address => address.AddressFamily)
            .OrderBy(group => group.Key == AddressFamily.InterNetwork ? 0 : 1)
            .SelectMany(group =>
            {
                var family = group.ToArray();
                var start = rotation < 0 ? Random.Shared.Next(family.Length) : rotation % family.Length;
                return family.Skip(start).Concat(family.Take(start));
            }).Take(2).ToArray();
    }

    internal static SharedPreferredRouteCandidate[] SelectSourceCandidates(
        IEnumerable<(string Host, IPAddress[] Addresses)> sources, int rotation = -1)
    {
        var groups = sources.Select(source => (source.Host, Addresses: SelectCandidates(source.Addresses, rotation)))
            .Where(source => source.Addresses.Length > 0).ToArray();
        if (groups.Length == 0) return [];
        var start = rotation < 0 ? Random.Shared.Next(groups.Length) : rotation % groups.Length;
        groups = groups.Skip(start).Concat(groups.Take(start)).ToArray();
        var first = new SharedPreferredRouteCandidate(groups[0].Addresses[0], groups[0].Host);
        // Give each source one turn regardless of whether it publishes two or twenty addresses.
        var otherSources = groups.Skip(1).SelectMany(source => source.Addresses
            .Select(address => new SharedPreferredRouteCandidate(address, source.Host)))
            .Where(candidate => !candidate.Address.Equals(first.Address)).ToArray();
        var second = otherSources.FirstOrDefault(candidate => !SameNetworkPrefix(candidate.Address, first.Address))
            ?? otherSources.FirstOrDefault()
            ?? groups[0].Addresses.Skip(1).Select(address => new SharedPreferredRouteCandidate(address, groups[0].Host)).FirstOrDefault();
        return second is null ? [first] : [first, second];
    }

    /// <summary>
    /// Picks at most one edge address from each configured source.  This is used by
    /// the initial read-only race; a source with many DNS records must not consume
    /// the whole probe budget or make a single IP a hard-coded preference.
    /// </summary>
    internal static SharedPreferredRouteCandidate[] SelectProbeSources(
        IEnumerable<(string Host, IPAddress[] Addresses)> sources, int rotation = -1, int maximumSources = 4)
    {
        if (maximumSources <= 0) return [];
        var groups = sources
            .Select(source => (source.Host, Address: SelectCandidates(source.Addresses, rotation).FirstOrDefault()))
            .Where(source => source.Address is not null)
            .Select(source => new SharedPreferredRouteCandidate(source.Address!, source.Host))
            .ToArray();
        if (groups.Length == 0) return [];

        var start = rotation < 0 ? Random.Shared.Next(groups.Length) : rotation % groups.Length;
        var ordered = groups.Skip(start).Concat(groups.Take(start));
        var seen = new HashSet<IPAddress>();
        return ordered.Where(candidate => seen.Add(candidate.Address)).Take(maximumSources).ToArray();
    }

    private static bool SameNetworkPrefix(IPAddress left, IPAddress right)
    {
        if (left.AddressFamily != right.AddressFamily) return false;
        var count = left.AddressFamily == AddressFamily.InterNetwork ? 3 : 6;
        return left.GetAddressBytes().AsSpan(0, count).SequenceEqual(right.GetAddressBytes().AsSpan(0, count));
    }

    internal static bool IsCloudflareEdge(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return false;
        var bytes = address.GetAddressBytes();
        foreach (var (prefix, bits) in EdgeRanges)
        {
            if (bytes.Length != prefix.Length) continue;
            var full = bits / 8;
            var remaining = bits % 8;
            if (bytes.AsSpan(0, full).SequenceEqual(prefix.AsSpan(0, full))
                && (remaining == 0 || (bytes[full] & (255 << (8 - remaining))) == prefix[full]))
                return true;
        }
        return false;
    }
}

internal sealed record SharedRouteDnsSourceDiagnostic(string Host, int ResolvedAddressCount,
    int AcceptedAddressCount, string Outcome);

internal sealed record SharedPreferredRouteCandidate(IPAddress Address, string Host);
