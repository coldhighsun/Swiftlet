using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Web;

namespace Swiftlet.Infrastructure.OAuth;

/// <summary>
/// A minimal HTTP server on the loopback interface that receives the OAuth redirect.
/// It listens on IPv4 and, when possible, IPv6 so both <c>127.0.0.1</c> and <c>localhost</c> redirects reach it.
/// </summary>
internal sealed class LoopbackCallbackServer : IDisposable
{
    /// <summary>
    /// The page shown in the browser after the redirect was received.
    /// </summary>
    private const string ResponsePage =
        "<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Swiftlet</title></head>"
        + "<body style=\"font-family:sans-serif;text-align:center;margin-top:20vh\">"
        + "<h2>Swiftlet</h2><p>Sign-in complete. You can close this window and return to the app.</p>"
        + "<p>登录完成，可以关闭此窗口并返回应用。</p></body></html>";

    /// <summary>
    /// How long a connected client may take to send its request line and headers.
    /// </summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The IPv4 listener.
    /// </summary>
    private readonly TcpListener _ipv4;

    /// <summary>
    /// The IPv6 listener, if the port was available on IPv6 as well.
    /// </summary>
    private readonly TcpListener? _ipv6;

    /// <summary>
    /// Initializes a new instance of the <see cref="LoopbackCallbackServer"/> class.
    /// </summary>
    /// <param name="ipv4">The started IPv4 listener.</param>
    /// <param name="ipv6">The started IPv6 listener, if any.</param>
    private LoopbackCallbackServer(TcpListener ipv4, TcpListener? ipv6)
    {
        _ipv4 = ipv4;
        _ipv6 = ipv6;
        Port = ((IPEndPoint)ipv4.LocalEndpoint).Port;
    }

    /// <summary>
    /// Gets the port the server listens on.
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// Starts a server on a free port.
    /// </summary>
    /// <returns>The running server.</returns>
    public static LoopbackCallbackServer Start()
    {
        var ipv4 = new TcpListener(IPAddress.Loopback, 0);
        ipv4.Start();
        var port = ((IPEndPoint)ipv4.LocalEndpoint).Port;

        TcpListener? ipv6 = null;
        if (Socket.OSSupportsIPv6)
        {
            try
            {
                ipv6 = new TcpListener(IPAddress.IPv6Loopback, port);
                ipv6.Start();
            }
            catch (SocketException)
            {
                // The same port may be taken on IPv6; IPv4 alone is enough for 127.0.0.1 redirects.
                ipv6 = null;
            }
        }

        return new LoopbackCallbackServer(ipv4, ipv6);
    }

    /// <summary>
    /// Waits for the browser to be redirected to the server and returns the query parameters of that request.
    /// Unrelated requests (such as favicon lookups) are answered with 404 and ignored.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The query parameters.</returns>
    public async Task<IReadOnlyDictionary<string, string>> WaitForCallbackAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            using var client = await AcceptAsync(cancellationToken);
            var parameters = await TryHandleAsync(client, cancellationToken);
            if (parameters is not null)
            {
                return parameters;
            }
        }
    }

    /// <summary>
    /// Stops listening.
    /// </summary>
    public void Dispose()
    {
        _ipv4.Stop();
        _ipv6?.Stop();
    }

    /// <summary>
    /// Accepts the next connection on either listener.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The accepted client.</returns>
    private async Task<TcpClient> AcceptAsync(CancellationToken cancellationToken)
    {
        if (_ipv6 is null)
        {
            return await _ipv4.AcceptTcpClientAsync(cancellationToken);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var first = _ipv4.AcceptTcpClientAsync(cts.Token).AsTask();
        var second = _ipv6.AcceptTcpClientAsync(cts.Token).AsTask();
        var winner = await Task.WhenAny(first, second);
        await cts.CancelAsync();

        // The loser was cancelled; if it nevertheless accepted a connection, dispose it.
        var loser = winner == first ? second : first;
        if (loser.IsCompletedSuccessfully)
        {
            loser.Result.Dispose();
        }

        return await winner;
    }

    /// <summary>
    /// Reads one request and answers it.
    /// </summary>
    /// <param name="client">The connection.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The query parameters when the request carries an OAuth response; otherwise <see langword="null"/>.</returns>
    private static async Task<IReadOnlyDictionary<string, string>?> TryHandleAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        var stream = client.GetStream();
        string? requestLine;
        try
        {
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            requestLine = await reader.ReadLineAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A client that connected but never sent a request must not block the sign-in.
            return null;
        }
        catch (IOException)
        {
            // The client went away mid-request; wait for the next connection.
            return null;
        }

        var parameters = ParseQuery(requestLine);
        var body = parameters is null ? string.Empty : ResponsePage;
        var status = parameters is null ? "404 Not Found" : "200 OK";
        var payload = Encoding.UTF8.GetBytes(body);
        var header = $"HTTP/1.1 {status}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken);
            await stream.WriteAsync(payload, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
        catch (IOException)
        {
            // The browser closed the connection after sending the request; the parameters were already read.
        }

        return parameters;
    }

    /// <summary>
    /// Extracts the query parameters of a <c>GET /?a=b HTTP/1.1</c> request line, if it carries any OAuth response.
    /// </summary>
    /// <param name="requestLine">The HTTP request line.</param>
    /// <returns>The parameters, or <see langword="null"/> if the request is not an OAuth redirect.</returns>
    private static Dictionary<string, string>? ParseQuery(string? requestLine)
    {
        var parts = requestLine?.Split(' ');
        if (parts is not { Length: >= 2 } || parts[0] != "GET")
        {
            return null;
        }

        var question = parts[1].IndexOf('?', StringComparison.Ordinal);
        if (question < 0)
        {
            return null;
        }

        var collection = HttpUtility.ParseQueryString(parts[1][(question + 1)..]);
        var result = collection.AllKeys
            .Where(k => k is not null)
            .ToDictionary(k => k!, k => collection[k] ?? string.Empty, StringComparer.Ordinal);
        return result.ContainsKey("code") || result.ContainsKey("error") ? result : null;
    }
}
