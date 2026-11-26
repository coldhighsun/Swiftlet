using Mediator;

namespace Swiftlet.Application.Sync;

/// <summary>
/// Synchronizes one folder of an account.
/// </summary>
/// <param name="AccountId">The account identifier.</param>
/// <param name="FolderId">The folder identifier.</param>
public sealed record SyncFolderCommand(Guid AccountId, Guid FolderId) : ICommand<FolderSyncResult>;

/// <summary>
/// Handles <see cref="SyncFolderCommand"/>.
/// </summary>
/// <param name="syncService">The synchronization service.</param>
/// <param name="coordinator">Serializes syncs per account.</param>
/// <param name="events">Receives the notification when the cache changed.</param>
public sealed class SyncFolderCommandHandler(MailSyncService syncService, SyncCoordinator coordinator, SyncEvents events)
    : ICommandHandler<SyncFolderCommand, FolderSyncResult>
{
    /// <inheritdoc />
    public async ValueTask<FolderSyncResult> Handle(SyncFolderCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // Under the account lock, so an account sync running at the same time cannot store the same new messages twice.
        var result = await coordinator.RunExclusiveAsync(
            command.AccountId,
            () => syncService.SyncSingleFolderAsync(command.AccountId, command.FolderId, cancellationToken),
            cancellationToken);
        if (result.Changed)
        {
            events.RaiseAccountSynced(command.AccountId, result.NewMessages);
        }

        return result;
    }
}
