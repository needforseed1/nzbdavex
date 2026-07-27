using Microsoft.AspNetCore.Http;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Utils;

public class ClientAddressUtilTests
{
    [Fact]
    public void PrefersTheForwardedClientWhenThePeerIsLocal()
    {
        // Playback arrives through a proxy on the same host, so without this
        // every viewer records as ::1 and two devices collapse into one session.
        var context = Context("::1", "203.0.113.7, 10.0.0.2");

        Assert.Equal("203.0.113.7", ClientAddressUtil.Resolve(context));
    }

    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("192.168.1.10")]
    [InlineData("172.16.9.9")]
    [InlineData("fd00::1")]
    public void TreatsPrivatePeersAsProxies(string peer)
    {
        var context = Context(peer, "203.0.113.7");

        Assert.Equal("203.0.113.7", ClientAddressUtil.Resolve(context));
    }

    [Fact]
    public void IgnoresAForwardedHeaderFromAPublicPeer()
    {
        // The header is attacker-controlled off the public internet, and this
        // value groups sessions: honouring it would let a caller scramble
        // someone else's playback history.
        var context = Context("198.51.100.4", "203.0.113.7");

        Assert.Equal("198.51.100.4", ClientAddressUtil.Resolve(context));
    }

    [Theory]
    [InlineData("203.0.113.7:51000", "203.0.113.7")]
    [InlineData("[2001:db8::5]:443", "2001:db8::5")]
    [InlineData("2001:db8::5", "2001:db8::5")]
    public void StripsPortsAndBrackets(string forwarded, string expected)
    {
        Assert.Equal(expected, ClientAddressUtil.Resolve(Context("::1", forwarded)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-address")]
    public void FallsBackToThePeerWhenTheHeaderIsUnusable(string forwarded)
    {
        Assert.Equal("::1", ClientAddressUtil.Resolve(Context("::1", forwarded)));
    }

    private static HttpContext Context(string peer, string? forwardedFor)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(peer);
        if (forwardedFor is not null) context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        return context;
    }
}
