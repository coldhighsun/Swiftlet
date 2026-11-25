namespace Swiftlet.Domain.Accounts;

/// <summary>
/// Host, port and TLS settings of a mail server.
/// </summary>
/// <param name="Host">The server host name.</param>
/// <param name="Port">The TCP port.</param>
/// <param name="UseSsl">Whether to use implicit TLS.</param>
public sealed record ServerEndpoint(string Host, int Port, bool UseSsl);
