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
public sealed class SyncAccountCommandHandler(MailSyncService syncService) : ICommandHandler<SyncAccountCommand, SyncResult>
{
    /// <inheritdoc />
    public async ValueTask<SyncResult> Handle(SyncAccountCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return await syncService.SyncAsync(command.AccountId, cancellationToken);
    }
}
