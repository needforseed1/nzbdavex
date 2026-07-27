using System.Net;
using Microsoft.AspNetCore.Http;

namespace NzbWebDAV.Utils;

/// <summary>
/// Resolves who is actually watching. Playback usually arrives through a proxy
/// on the same host, so the socket peer is a loopback address for every viewer
/// and cannot tell two devices apart. A forwarded address is preferred when the
/// direct peer is local infrastructure.
/// </summary>
public static class ClientAddressUtil
{
    /// <summary>
    /// Only consulted when the immediate peer is loopback or private. A
    /// forwarded header from the public internet is attacker-controlled, and
    /// this value labels sessions and groups them, so a spoofed one would let a
    /// caller scramble another viewer's history.
    /// </summary>
    public static string? Resolve(HttpContext context)
    {
        var direct = context.Connection.RemoteIpAddress;
        if (direct is null || !IsLocalPeer(direct)) return direct?.ToString();

        var forwarded = context.Request.Headers["X-Forwarded-For"].ToString();
        if (string.IsNullOrWhiteSpace(forwarded)) return direct.ToString();

        // Left-most entry is the original client; the rest are proxy hops.
        var original = forwarded.Split(',')[0].Trim();
        if (original.Length == 0) return direct.ToString();

        // A bracketed IPv6 literal may carry a port, as may IPv4:port.
        if (original.StartsWith('[') && original.Contains(']'))
            original = original[1..original.IndexOf(']')];
        else if (original.Count(c => c == ':') == 1)
            original = original[..original.IndexOf(':')];

        return IPAddress.TryParse(original, out var parsed) ? parsed.ToString() : direct.ToString();
    }

    private static bool IsLocalPeer(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();
        return bytes.Length switch
        {
            // 10/8, 172.16/12, 192.168/16
            4 => bytes[0] == 10
                 || (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31)
                 || (bytes[0] == 192 && bytes[1] == 168),
            // fc00::/7 unique-local
            16 => (bytes[0] & 0xFE) == 0xFC,
            _ => false,
        };
    }
}
