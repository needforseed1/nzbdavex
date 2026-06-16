using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace NzbWebDAV.Utils;

/// <summary>
/// One shared HttpClient per distinct proxy URL (or "" for direct). Returned clients
/// have an infinite Timeout because they're shared across callers with different
/// budgets; each caller enforces its own per-request timeout via CancellationToken.
/// </summary>
public static class ProxyHttpClientPool
{
    private static readonly ConcurrentDictionary<string, HttpClient> Clients = new();

    public static HttpClient GetClient(string? proxyUrl)
    {
        var key = Normalize(proxyUrl) ?? "";
        return Clients.GetOrAdd(key, k =>
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
                ConnectTimeout = TimeSpan.FromSeconds(15),
            };

            if (k.Length > 0 && Uri.TryCreate(k, UriKind.Absolute, out var uri))
            {
                var address = new UriBuilder(uri) { UserName = "", Password = "" }.Uri;
                var proxy = new WebProxy(address) { BypassProxyOnLocal = false };
                if (!string.IsNullOrEmpty(uri.UserInfo))
                {
                    var creds = uri.UserInfo.Split(':', 2);
                    proxy.Credentials = new NetworkCredential(
                        Uri.UnescapeDataString(creds[0]),
                        creds.Length > 1 ? Uri.UnescapeDataString(creds[1]) : string.Empty);
                }
                handler.Proxy = proxy;
                handler.UseProxy = true;
            }
            else
            {
                handler.UseProxy = false;
                handler.ConnectCallback = KeepAliveConnectAsync;
            }

            return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        });
    }

    private static async ValueTask<Stream> KeepAliveConnectAsync(
        SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        TrySetKeepAliveTuning(socket);
        try
        {
            await socket.ConnectAsync(context.DnsEndPoint, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static void TrySetKeepAliveTuning(Socket socket)
    {
        try { socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30); }
        catch { }
        try { socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5); }
        catch { }
        try { socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3); }
        catch { }
    }

    private static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return null;
        return uri.ToString();
    }
}
