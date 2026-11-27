using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Swiftlet.Application.Abstractions;
using Swiftlet.Domain.Accounts;

namespace Swiftlet.Infrastructure.Mail;

/// <summary>
/// Opens IMAP connections with MailKit.
/// </summary>
/// <param name="secrets">The secret store holding account passwords.</param>
/// <param name="accessTokens">The OAuth 2.0 access token provider.</param>
/// <param name="logger">The logger handed to the sessions.</param>
internal sealed class MailKitImapSessionFactory(
    ISecretStore secrets,
    IAccessTokenProvider accessTokens,
    ILogger<MailKitImapSession> logger) : IImapSessionFactory
{
    /// <summary>
    /// The network timeout for IMAP operations.
    /// </summary>
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public Task<IImapSession> ConnectAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        return OpenAsync(account, null, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IImapSession> ConnectAsync(MailAccount account, string password, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentException.ThrowIfNullOrEmpty(password);
        return OpenAsync(account, password, cancellationToken);
    }

    /// <summary>
    /// Connects and authenticates.
    /// </summary>
    /// <param name="account">The account.</param>
    /// <param name="password">The password to use instead of the stored one, if any.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The open session.</returns>
    private async Task<IImapSession> OpenAsync(MailAccount account, string? password, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Connecting to IMAP {Host}:{Port} for {Account} ({Auth})",
            account.Imap.Host,
            account.Imap.Port,
            account.EmailAddress,
            account.AuthMethod);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var client = new ImapClient { Timeout = (int)NetworkTimeout.TotalMilliseconds };
        try
        {
            var options = account.Imap.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
            await client.ConnectAsync(account.Imap.Host, account.Imap.Port, options, cancellationToken);

            // Some providers (notably NetEase 163/126) reject login unless the client identifies itself first.
            if (client.Capabilities.Contains(ImapCapability.Id))
            {
                await client.IdentifyAsync(new ImapImplementation { Name = "Swiftlet", Version = "1.0" }, cancellationToken);
            }

            await MailAuthentication.AuthenticateAsync(client, account, secrets, accessTokens, cancellationToken, password);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up (another message was selected, the application is closing); that is not a connection problem, so no warning and no stack trace.
            client.Dispose();
            logger.LogInformation("IMAP connection for {Account} was cancelled after {ElapsedMs} ms", account.EmailAddress, started.ElapsedMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            // Release the socket when connecting or logging in fails; the error still propagates.
            client.Dispose();
            logger.LogWarning(ex, "IMAP connection for {Account} failed after {ElapsedMs} ms", account.EmailAddress, started.ElapsedMilliseconds);
            throw;
        }

        logger.LogInformation("IMAP connected for {Account} in {ElapsedMs} ms", account.EmailAddress, started.ElapsedMilliseconds);
        return new MailKitImapSession(client, logger);
    }
}
