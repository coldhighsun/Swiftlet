using Mediator;
using Microsoft.Extensions.Logging;
using Swiftlet.Application.Abstractions;
using Swiftlet.Domain.Accounts;

namespace Swiftlet.Application.Accounts;

/// <summary>
/// Deletes an account and all of its locally cached mail and stored credentials.
/// </summary>
/// <param name="AccountId">The account identifier.</param>
public sealed record DeleteAccountCommand(Guid AccountId) : ICommand<Unit>;

/// <summary>
/// Handles <see cref="DeleteAccountCommand"/>.
/// </summary>
/// <param name="accounts">The account repository.</param>
/// <param name="secrets">The secret store.</param>
/// <param name="logger">The logger; optional.</param>
public sealed class DeleteAccountCommandHandler(
    IAccountRepository accounts,
    ISecretStore secrets,
    ILogger<DeleteAccountCommandHandler>? logger = null)
    : ICommandHandler<DeleteAccountCommand, Unit>
{
    /// <inheritdoc />
    public async ValueTask<Unit> Handle(DeleteAccountCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        logger?.LogInformation("Removing account {AccountId} and its local data", command.AccountId);

        // Rows go first: a leftover secret is harmless, but an account without its credentials would be a broken entry.
        // A missing row is not an early exit: a retry after a failed secret removal must still clean up.
        await accounts.DeleteAsync(command.AccountId, cancellationToken);

        // Both keys are removed (removal is idempotent), so the handler does not depend on the row that is already gone.
        await secrets.RemoveAsync(AccountSecrets.PasswordKey(command.AccountId), cancellationToken);
        await secrets.RemoveAsync(AccountSecrets.OAuthKey(command.AccountId), cancellationToken);
        logger?.LogInformation("Account {AccountId} removed", command.AccountId);
        return Unit.Value;
    }
}
