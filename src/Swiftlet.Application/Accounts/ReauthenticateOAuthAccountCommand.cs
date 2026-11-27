using System.Text.Json;
using Mediator;
using Microsoft.Extensions.Logging;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Logging;
using Swiftlet.Domain.Accounts;

namespace Swiftlet.Application.Accounts;

/// <summary>
/// Signs an existing OAuth account in again in the browser and replaces its stored tokens.
/// </summary>
/// <param name="AccountId">The account identifier.</param>
public sealed record ReauthenticateOAuthAccountCommand(Guid AccountId) : ICommand<Unit>, IAccountRequest;

/// <summary>
/// Handles <see cref="ReauthenticateOAuthAccountCommand"/>.
/// </summary>
/// <param name="accounts">The account repository.</param>
/// <param name="secrets">The secret store.</param>
/// <param name="authorizer">The interactive OAuth sign-in.</param>
/// <param name="imapFactory">The IMAP connection factory used to verify the new tokens.</param>
/// <param name="logger">The logger.</param>
public sealed class ReauthenticateOAuthAccountCommandHandler(
    IAccountRepository accounts,
    ISecretStore secrets,
    IOAuthAuthorizer authorizer,
    IImapSessionFactory imapFactory,
    ILogger<ReauthenticateOAuthAccountCommandHandler> logger) : ICommandHandler<ReauthenticateOAuthAccountCommand, Unit>
{
    /// <inheritdoc />
    public async ValueTask<Unit> Handle(ReauthenticateOAuthAccountCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var account = await accounts.GetAsync(command.AccountId, cancellationToken)
            ?? throw new InvalidOperationException($"Account {command.AccountId} does not exist.");
        if (account.AuthMethod != AuthMethod.OAuth2 || account.OAuthProvider is not { } provider)
        {
            throw new InvalidOperationException($"Account {account.EmailAddress} does not use OAuth sign-in.");
        }

        var authorization = await authorizer.AuthorizeAsync(provider, cancellationToken);
        var signedIn = authorization.EmailAddress.Trim();
        if (!string.Equals(signedIn, account.EmailAddress, StringComparison.OrdinalIgnoreCase))
        {
            // Reject before touching the stored tokens: signing in as someone else must not replace this account's credentials.
            throw new InvalidOperationException($"Signed in as {signedIn} but the account is {account.EmailAddress}.");
        }

        // The browser wait can take minutes: the account may have been removed meanwhile, and a background refresh may have rotated the tokens.
        if (await accounts.GetAsync(account.Id, cancellationToken) is null)
        {
            throw new InvalidOperationException($"Account {account.EmailAddress} was removed during sign-in.");
        }

        // Loaded right before the overwrite so a restore puts back the latest tokens, not those from before the browser wait.
        OAuthTokens? previous;
        try
        {
            previous = await OAuthTokenStorage.LoadAsync(secrets, account.Id, cancellationToken);
        }
        catch (JsonException)
        {
            // A corrupt or stale stored value is exactly when re-sign-in is needed, so treat it as "no previous tokens".
            previous = null;
        }

        await OAuthTokenStorage.SaveAsync(secrets, account.Id, authorization.Tokens, cancellationToken);
        try
        {
            await using var session = await imapFactory.ConnectAsync(account, cancellationToken);
        }
        catch (Exception verificationFailure)
        {
            // Put the working credentials back so a failed re-sign-in does not make things worse; the error still propagates.
            try
            {
                if (previous is null)
                {
                    await secrets.RemoveAsync(AccountSecrets.OAuthKey(account.Id), CancellationToken.None);
                }
                else
                {
                    await OAuthTokenStorage.SaveAsync(secrets, account.Id, previous, CancellationToken.None);
                }
            }
#pragma warning disable CA1031 // Any secret-store failure must not replace the original verification error.
            catch (Exception restoreFailure)
#pragma warning restore CA1031
            {
                // The verification error is the one the user needs to see; keep the restore failure as context on it.
                verificationFailure.Data["RestoreFailure"] = restoreFailure.Message;
                logger.LogError(restoreFailure, "Could not restore the previous OAuth tokens of account {Account} after a failed re-sign-in.", account.EmailAddress);
            }

            throw;
        }

        return Unit.Value;
    }
}
