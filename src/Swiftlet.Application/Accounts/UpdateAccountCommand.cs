using Mediator;
using Microsoft.Extensions.Logging;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Sync;
using Swiftlet.Domain.Accounts;

namespace Swiftlet.Application.Accounts;

/// <summary>
/// Changes the settings of an existing account. A changed IMAP server or password is verified with a sign-in before
/// anything is stored.
/// </summary>
/// <param name="AccountId">The account identifier.</param>
/// <param name="DisplayName">The name typed by the user; blank for none.</param>
/// <param name="SyncInterval">The interval between background synchronizations.</param>
/// <param name="Imap">The IMAP endpoint; omitted to keep the current one.</param>
/// <param name="Smtp">The SMTP endpoint; omitted to keep the current one.</param>
/// <param name="Password">The new password; omitted or empty to keep the current one.</param>
public sealed record UpdateAccountCommand(
    Guid AccountId,
    string DisplayName,
    TimeSpan SyncInterval,
    ServerEndpoint? Imap = null,
    ServerEndpoint? Smtp = null,
    string? Password = null) : ICommand<UpdateAccountResult>;

/// <summary>
/// The outcome of <see cref="UpdateAccountCommand"/>.
/// </summary>
/// <param name="ConnectionChanged">Whether the IMAP server or the password changed, so the account should be synchronized.</param>
public sealed record UpdateAccountResult(bool ConnectionChanged);

/// <summary>
/// Handles <see cref="UpdateAccountCommand"/>.
/// </summary>
/// <param name="accounts">The account repository.</param>
/// <param name="secrets">The secret store.</param>
/// <param name="imapFactory">The IMAP connection factory used to verify new settings.</param>
/// <param name="revisions">Tells the background services about changed connection settings.</param>
/// <param name="logger">The logger; optional.</param>
public sealed class UpdateAccountCommandHandler(
    IAccountRepository accounts,
    ISecretStore secrets,
    IImapSessionFactory imapFactory,
    AccountConnectionRevisions revisions,
    ILogger<UpdateAccountCommandHandler>? logger = null) : ICommandHandler<UpdateAccountCommand, UpdateAccountResult>
{
    /// <inheritdoc />
    public async ValueTask<UpdateAccountResult> Handle(UpdateAccountCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var account = await accounts.GetAsync(command.AccountId, cancellationToken)
            ?? throw new InvalidOperationException($"Account {command.AccountId} does not exist.");
        var password = string.IsNullOrEmpty(command.Password) ? null : command.Password;
        if (password is not null && account.AuthMethod != AuthMethod.Password)
        {
            throw new InvalidOperationException($"Account {account.EmailAddress} does not use a password.");
        }

        var imap = command.Imap ?? account.Imap;
        var smtp = command.Smtp ?? account.Smtp;
        var imapChanged = imap != account.Imap;
        var smtpChanged = smtp != account.Smtp;

        // Nothing is saved until the end, so a failed validation or verification leaves the account as it was.
        account.ChangeDisplayName(command.DisplayName);
        account.ChangeSyncInterval(command.SyncInterval);
        if (imapChanged || smtpChanged)
        {
            // An unchanged endpoint keeps its instance, so it is not written again.
            account.ChangeServers(imapChanged ? imap : account.Imap, smtpChanged ? smtp : account.Smtp);
        }

        var connectionChanged = imapChanged || password is not null;
        if (connectionChanged)
        {
            await using var session = password is null
                ? await imapFactory.ConnectAsync(account, cancellationToken)
                : await imapFactory.ConnectAsync(account, password, cancellationToken);
        }

        // From here on the changes are written completely: cancelling between the two stores would leave them out of step.
        cancellationToken.ThrowIfCancellationRequested();
        if (password is null)
        {
            await accounts.UpdateAsync(account, CancellationToken.None);
        }
        else
        {
            await SaveWithPasswordAsync(account, password);
        }

        if (connectionChanged)
        {
            revisions.Bump(account.Id);
        }

        logger?.LogInformation("Account {Account} updated (connection changed: {ConnectionChanged})", account.EmailAddress, connectionChanged);
        return new UpdateAccountResult(connectionChanged);
    }

    /// <summary>
    /// Stores the new password and the account, putting the previous password back if the account cannot be saved.
    /// </summary>
    /// <param name="account">The changed account.</param>
    /// <param name="password">The verified password.</param>
    /// <returns>A task that completes when both are stored.</returns>
    private async Task SaveWithPasswordAsync(MailAccount account, string password)
    {
        var key = AccountSecrets.PasswordKey(account.Id);
        var previous = await secrets.GetAsync(key, CancellationToken.None);
        await secrets.SetAsync(key, password, CancellationToken.None);
        try
        {
            await accounts.UpdateAsync(account, CancellationToken.None);
        }
        catch (Exception saveFailure)
        {
            // The old settings stay in effect, so the old password must too; the save error still propagates.
            try
            {
                if (previous is null)
                {
                    await secrets.RemoveAsync(key, CancellationToken.None);
                }
                else
                {
                    await secrets.SetAsync(key, previous, CancellationToken.None);
                }
            }
#pragma warning disable CA1031 // Any secret-store failure must not replace the original save error.
            catch (Exception restoreFailure)
#pragma warning restore CA1031
            {
                // The save error is the one the user needs to see; keep the restore failure as context on it.
                saveFailure.Data["RestoreFailure"] = restoreFailure.Message;
                logger?.LogError(restoreFailure, "Could not restore the previous password of account {Account} after a failed save.", account.EmailAddress);
            }

            throw;
        }
    }
}
