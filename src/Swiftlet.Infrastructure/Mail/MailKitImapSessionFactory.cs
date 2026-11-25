using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Swiftlet.Application.Abstractions;
using Swiftlet.Domain.Accounts;

namespace Swiftlet.Infrastructure.Mail;

/// <summary>
/// Opens IMAP connections with MailKit.
/// </summary>
/// <param name="secrets">The secret store holding account passwords.</param>
/// <param name="accessTokens">The OAuth 2.0 access token provider.</param>
internal sealed class MailKitImapSessionFactory(ISecretStore secrets, IAccessTokenProvider accessTokens) : IImapSessionFactory
{
    /// <summary>
    /// The network timeout for IMAP operations.
    /// </summary>
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public async Task<IImapSession> ConnectAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

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

            await MailAuthentication.AuthenticateAsync(client, account, secrets, accessTokens, cancellationToken);
        }
        catch
        {
            // Release the socket when connecting or logging in fails; the error still propagates.
            client.Dispose();
            throw;
        }

        return new MailKitImapSession(client);
    }
}
