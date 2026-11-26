using Mediator;

namespace Swiftlet.Application.Sync;

/// <summary>
/// Synchronizes one account.
/// </summary>
/// <param name="AccountId">The account identifier.</param>
public sealed record SyncAccountCommand(Guid AccountId) : ICommand<SyncResult>;

/// <summary>
/// Handles <see cref="SyncAccountCommand"/>.
/// </summary>
/// <param name="syncService">The synchronization service.</param>
/// <param name="coordinator">Serializes syncs per account.</param>
/// <param name="events">Receives the completion notification.</param>
public sealed class SyncAccountCommandHandler(MailSyncService syncService, SyncCoordinator coordinator, SyncEvents events)
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
            () => syncService.SyncAsync(
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
}
