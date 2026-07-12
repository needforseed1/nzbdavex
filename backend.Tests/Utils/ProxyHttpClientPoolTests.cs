using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Utils;

public class ProxyHttpClientPoolTests
{
    [Theory]
    [InlineData("not-a-url")]
    [InlineData("socks5://proxy.example:1080")]
    public void InvalidConfiguredProxyDoesNotSilentlyUseDirectConnection(string proxy)
    {
        Assert.Throws<ArgumentException>(() => ProxyHttpClientPool.GetClient(proxy));
    }
}
