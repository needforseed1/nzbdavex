using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using UsenetSharp.Exceptions;
using UsenetSharp.Models;

namespace UsenetSharp.Clients;

public partial class UsenetClient
{
    public async Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
    {
        // Clean up any existing connection
        CleanupConnection();
        await _commandLock.WaitAsync(cancellationToken);
        using var operationCts = CreateOperationCts(cancellationToken);
        var operationToken = operationCts.Token;
        try
        {
            var tcpClient = new TcpClient();
            _tcpClient = tcpClient;
            ConfigureTcpKeepAlive(tcpClient);

            // Some DNS/TCP/TLS implementations take longer than expected to
            // observe cancellation. Closing the socket is the reliable escape
            // hatch and ensures a cancelled speculative connection cannot retain
            // a shared handshake reservation indefinitely.
            using var abortRegistration = operationToken.Register(static state =>
            {
                try
                {
                    ((TcpClient)state!).Dispose();
                }
                catch
                {
                    // Cancellation is best-effort; the operation below still
                    // observes the token and normal disposal runs on failure.
                }
            }, tcpClient);

            await tcpClient.ConnectAsync(host, port, operationToken);
            _stream = tcpClient.GetStream();

            if (useSsl)
            {
                var sslStream = new SslStream(_stream, false);
                await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = host,
                    EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 |
                                          System.Security.Authentication.SslProtocols.Tls13,
                    CertificateRevocationCheckMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.Online,
                }, operationToken);
                _stream = sslStream;
            }

            // Use Latin1 encoding to preserve exact byte values 0-255 for yEnc-encoded content
            _reader = new StreamReader(_stream, Encoding.Latin1);
            _writer = new StreamWriter(_stream, Encoding.Latin1) { AutoFlush = true };

            // Read the server response
            var response = await ReadLineAsync(operationToken);
            var responseCode = ParseResponseCode(response);

            // NNTP servers typically respond with "200" or "201" for successful connection
            if (responseCode != (int)UsenetResponseType.ServerReadyPostingAllowed &&
                responseCode != (int)UsenetResponseType.ServerReadyNoPostingAllowed)
                throw new UsenetConnectionException(response!) { ResponseCode = responseCode };
        }
        catch (Exception e) when (operationToken.IsCancellationRequested &&
                                  e is not OperationCanceledException)
        {
            // Socket disposal may surface as IOException/ObjectDisposedException
            // rather than OperationCanceledException. Preserve cancellation so
            // the pool releases its creation reservation instead of treating the
            // abort as a provider failure.
            throw new OperationCanceledException(
                "NNTP connection setup was cancelled.", e, operationToken);
        }
        finally
        {
            _commandLock.Release();
        }
    }

    private static void ConfigureTcpKeepAlive(TcpClient client)
    {
        try
        {
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30);
            client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
            client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch (SocketException)
        {
            // Platform defaults are still safe; command-level timeouts remain active.
        }
        catch (PlatformNotSupportedException)
        {
            // Some runtimes expose KeepAlive without the timing controls.
        }
    }
}
