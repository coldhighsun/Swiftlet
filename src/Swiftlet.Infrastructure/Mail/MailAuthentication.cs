using MailKit;
using MailKit.Security;
using Swiftlet.Application.Abstractions;
using Swiftlet.Domain.Accounts;

namespace Swiftlet.Infrastructure.Mail;

/// <summary>
/// Authenticates MailKit clients for password and OAuth 2.0 (XOAUTH2) accounts.
/// </summary>
internal static class MailAuthentication
{
    /// <summary>
    /// Signs in a connected client with the credentials of an account.
    /// </summary>
    /// <param name="client">The connected IMAP or SMTP client.</param>
    /// <param name="account">The account.</param>
    /// <param name="secrets">The secret store holding passwords.</param>
    /// <param name="accessTokens">The OAuth 2.0 access token provider.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the client is authenticated.</returns>
    public static async Task AuthenticateAsync(
        MailService client,
        MailAccount account,
        ISecretStore secrets,
        IAccessTokenProvider accessTokens,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(account);

        if (account.AuthMethod == AuthMethod.OAuth2)
        {
            var token = await accessTokens.GetAccessTokenAsync(account, cancellationToken);
            await client.AuthenticateAsync(new SaslMechanismOAuth2(account.EmailAddress, token), cancellationToken);
            return;
        }

        var password = await secrets.GetAsync(AccountSecrets.PasswordKey(account.Id), cancellationToken)
            ?? throw new InvalidOperationException($"No password is stored for account {account.EmailAddress}.");
        await client.AuthenticateAsync(account.EmailAddress, password, cancellationToken);
    }
}
