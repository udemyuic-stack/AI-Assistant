using System.Net;
using System.Net.Sockets;

namespace AiAssistant.Api.Services;

// Blocks the scraper from reaching internal infrastructure. An admin-entered
// site URL is still user input - one pointed at http://169.254.169.254/ or
// http://localhost:5433/ would make this server fetch cloud metadata or probe
// its own database port and hand the response back as "scraped content".
// Checked at actual connect time (not just once against the URL string), so
// a DNS answer that changes between validation and connection can't bypass
// it (DNS rebinding).
public static class PrivateNetworkGuard
{
    public static bool IsBlocked(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal) return true;

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None)) return true;
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes switch
        {
            [10, ..] => true,                                  // 10.0.0.0/8
            [172, var b, ..] when b is >= 16 and <= 31 => true, // 172.16.0.0/12
            [192, 168, ..] => true,                             // 192.168.0.0/16
            [169, 254, ..] => true,                             // 169.254.0.0/16 - link-local, incl. cloud metadata
            [0, ..] => true,                                    // 0.0.0.0/8
            [100, var b, ..] when b is >= 64 and <= 127 => true, // 100.64.0.0/10 - CGNAT
            _ => false
        };
    }
}
