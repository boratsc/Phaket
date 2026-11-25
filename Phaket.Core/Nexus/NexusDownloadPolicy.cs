using System.Net;
using System.Net.Sockets;

namespace Phaket.Core.Nexus;

// PAK-129: the download address comes from an API response, so it is untrusted input. Only
// Nexus download hosts are fetched, every redirect hop is checked again, and literal addresses
// in private, loopback and link-local ranges are refused (SSRF into the local network).
// Loopback is opened only for the offline test stub by an explicit caller flag.
public static class NexusDownloadPolicy
{
    // Verified against real download_link payloads: cf-files.nexusmods.com and the
    // supporter-files/files mirrors under nexus-cdn.com.
    private static readonly string[] AllowedHostSuffixes = ["nexusmods.com", "nexus-cdn.com"];

    public const int MaxRedirects = 5;

    public static bool IsAllowed(Uri uri, bool allowLoopback = false)
    {
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
        string host = uri.IdnHost;
        if (host.Length == 0) return false;
        if (IsLoopbackHost(host))
            return allowLoopback && uri.Scheme is "http" or "https";
        if (IPAddress.TryParse(host, out var literal) && IsPrivateAddress(literal)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort) return false;
        return AllowedHostSuffixes.Any(suffix =>
            host.Equals(suffix, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);

    // Suffix matching is not enough for a literal address: 127.0.0.1 must never be fetched
    // even when a future allowlist entry or a redirect points there.
    private static bool IsPrivateAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal ||
                   address.Equals(IPAddress.IPv6Any);
        byte[] bytes = address.GetAddressBytes();
        return bytes[0] is 0 or 10 or 127 ||
               bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
               bytes[0] == 192 && bytes[1] == 168 ||
               bytes[0] == 169 && bytes[1] == 254 ||
               bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
    }
}