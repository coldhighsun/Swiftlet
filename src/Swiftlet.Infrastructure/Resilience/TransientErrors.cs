using System.Net;
using System.Net.Sockets;
using MailKit;

namespace Swiftlet.Infrastructure.Resilience;

/// <summary>
/// Tells failures that may pass by themselves (a lost connection, a busy server) from those that repeating cannot fix.
/// </summary>
internal static class TransientErrors
{
    /// <summary>
    /// Determines whether an operation that failed with this error is worth repeating. Authentication failures are not:
    /// repeating a wrong password can lock the account.
    /// </summary>
    /// <param name="exception">The failure.</param>
    /// <returns><see langword="true"/> if the failure may pass by itself.</returns>
    public static bool IsTransient(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            HttpRequestException http => http.StatusCode is not { } status || IsTransient(status),

            // HttpClient reports its own timeout as a cancellation wrapping a TimeoutException.
            OperationCanceledException { InnerException: TimeoutException } => true,
            IOException or SocketException or TimeoutException => true,
            ServiceNotConnectedException or ProtocolException => true,
            _ => false,
        };
    }

    /// <summary>
    /// Determines whether an HTTP answer asks the client to try again later.
    /// </summary>
    /// <param name="status">The status code.</param>
    /// <returns><see langword="true"/> for server errors, request timeouts and rate limiting.</returns>
    public static bool IsTransient(HttpStatusCode status)
    {
        return (int)status >= 500 || status is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests;
    }
}
