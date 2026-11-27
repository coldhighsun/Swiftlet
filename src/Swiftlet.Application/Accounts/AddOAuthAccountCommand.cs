using Mediator;
using Microsoft.Extensions.Logging;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Logging;
using Swiftlet.Domain.Accounts;

namespace Swiftlet.Application.Accounts;

/// <summary>
/// Adds an account after signing in with an OAuth 2.0 provider in the browser.
/// </summary>
/// <param name="Provider">The identity provider.</param>
/// <param name="DisplayName">The name typed by the user; defaults to the name reported by the provider.</param>
public sealed record AddOAuthAccountCommand(OAuthProvider Provider, string? DisplayName = null) : ICommand<Guid>, ILogDetails
{
    /// <inheritdoc />
    public string DescribeForLog(LogNames names)
    {
        return $"{Provider} sign-in";
    }
}

/// <summary>
/// Handles <see cref="AddOAuthAccountCommand"/>.
/// </summary>
/// <param name="accounts">The account repository.</param>
/// <param name="secrets">The secret store.</param>
/// <param name="authorizer">The interactive OAuth sign-in.</param>
/// <param name="imapFactory">The IMAP connection factory used to verify the tokens.</param>
/// <param name="timeProvider">The clock.</param>
/// <param name="logger">The logger; optional.</param>
public sealed class AddOAuthAccountCommandHandler(
    IAccountRepository accounts,
    ISecretStore secrets,
    IOAuthAuthorizer authorizer,
    IImapSessionFactory imapFactory,
    TimeProvider timeProvider,
    ILogger<AddOAuthAccountCommandHandler>? logger = null) : ICommandHandler<AddOAuthAccountCommand, Guid>
{
    /// <inheritdoc />
    public async ValueTask<Guid> Handle(AddOAuthAccountCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var authorization = await authorizer.AuthorizeAsync(command.Provider, cancellationToken);
        var email = authorization.EmailAddress.Trim();
        var existing = await accounts.GetAllAsync(cancellationToken);
        if (existing.Any(a => string.Equals(a.EmailAddress, email, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Account {email} already exists.");
        }

        var preset = ProviderPresets.For(command.Provider, authorization.IsOrganizationAccount);
        var hasCustomName = !string.IsNullOrWhiteSpace(command.DisplayName);
        var displayName = hasCustomName
            ? command.DisplayName!
            : !string.IsNullOrWhiteSpace(authorization.DisplayName) ? authorization.DisplayName : email[..email.IndexOf('@')];
        var account = MailAccount.Create(
            displayName,
            email,
            AuthMethod.OAuth2,
            preset.Imap,
            preset.Smtp,
            timeProvider.GetUtcNow(),
            command.Provider,
            hasCustomName);

        await OAuthTokenStorage.SaveAsync(secrets, account.Id, authorization.Tokens, cancellationToken);
        try
        {
            await using var session = await imapFactory.ConnectAsync(account, cancellationToken);
            await accounts.AddAsync(account, cancellationToken);
        }
        catch
        {
            // Do not leave orphaned tokens behind when verification or saving fails; the error still propagates.
            await secrets.RemoveAsync(AccountSecrets.OAuthKey(account.Id), CancellationToken.None);
            throw;
        }

        logger?.LogInformation("Account {Account} added ({Provider} sign-in)", email, command.Provider);
        return account.Id;
    }
}
