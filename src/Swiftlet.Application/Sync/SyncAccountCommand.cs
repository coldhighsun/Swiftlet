using Mediator;
using Swiftlet.Application.Logging;

namespace Swiftlet.Application.Sync;

/// <summary>
/// Synchronizes one account.
/// </summary>
/// <param name="AccountId">The account identifier.</param>
public sealed record SyncAccountCommand(Guid AccountId) : ICommand<SyncResult>, IAccountRequest;

/// <summary>
/// Handles <see cref="SyncAccountCommand"/>.
/// </summary>
/// <param name="syncService">The synchronization service.</param>
/// <param name="coordinator">Serializes syncs per account.</param>
/// <param name="events">Receives the completion notification.</param>
/// <param name="statuses">Records how the sync went.</param>
public sealed class SyncAccountCommandHandler(
    MailSyncService syncService,
    SyncCoordinator coordinator,
    SyncEvents events,
    AccountSyncStatusTracker statuses)
    : ICommandHandler<SyncAccountCommand, SyncResult>
{
    /// <inheritdoc />
    public async ValueTask<SyncResult> Handle(SyncAccountCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        // The INBOX is synchronized first and announced at once, so new, read and deleted mail shows without waiting for the other folders.
        var announced = 0;
        var result = await coordinator.RunExclusiveAsync(
            command.AccountId,
            () => SyncRecordedAsync(
                command.AccountId,
                newMessages =>
                {
                    announced = newMessages;
                    events.RaiseAccountSynced(command.AccountId, newMessages);
                },
                cancellationToken),
            cancellationToken);
        if (result.FoldersSynced > 0)
        {
            // Only the messages not announced yet, so listeners can add the counts up.
            events.RaiseAccountSynced(command.AccountId, result.NewMessages - announced);
        }

        return result;
    }

    /// <summary>
    /// Synchronizes an account and records the status; called while the account is locked, so only a sync that is really
    /// running is reported as running.
    /// </summary>
    /// <param name="accountId">The account.</param>
    /// <param name="inboxSynced">Called when the INBOX changed and further folders follow.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result of the sync.</returns>
    private async Task<SyncResult> SyncRecordedAsync(Guid accountId, Action<int> inboxSynced, CancellationToken cancellationToken)
    {
        statuses.Started(accountId);
        events.RaiseSyncStatusChanged(accountId);
        try
        {
            var result = await syncService.SyncAsync(accountId, inboxSynced, cancellationToken);
            statuses.Succeeded(accountId, result.FailedFolders);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Abandoned by the caller, so there is no outcome to record.
            statuses.Cancelled(accountId);
            throw;
        }
        catch (Exception ex)
        {
            // Recorded for the account page only; the caller still handles the failure.
            statuses.Failed(accountId, ex);
            throw;
        }
        finally
        {
            events.RaiseSyncStatusChanged(accountId);
        }
    }
}
