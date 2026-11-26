using Mediator;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Sync;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Application.Messages;

/// <summary>
/// How a message was removed from the server.
/// </summary>
public enum DeleteOutcome
{
    /// <summary>
    /// The message was moved to the account's Trash folder.
    /// </summary>
    MovedToTrash,

    /// <summary>
    /// The message was permanently deleted.
    /// </summary>
    DeletedPermanently,
}

/// <summary>
/// Deletes a message on the IMAP server and then removes the cached copy.
/// </summary>
/// <param name="AccountId">The account that owns the message.</param>
/// <param name="MessageId">The cached message identifier.</param>
public sealed record DeleteMessageCommand(Guid AccountId, Guid MessageId) : ICommand<DeleteOutcome>;

/// <summary>
/// Handles <see cref="DeleteMessageCommand"/>.
/// When the server moved the message to Trash and the cached Trash folder is provably complete, the cached row is re-homed
/// into Trash instead of being deleted, so the next sync does not download the same message again.
/// </summary>
/// <param name="messages">The message repository.</param>
/// <param name="folders">The folder repository.</param>
/// <param name="accounts">The account repository.</param>
/// <param name="imapFactory">The IMAP connection factory.</param>
/// <param name="coordinator">Serializes the local Trash update with the regular syncs of the account.</param>
/// <param name="lockWait">How long the local Trash update waits for a running sync before it falls back to deleting the cached row; defaults to 2 seconds.</param>
public sealed class DeleteMessageCommandHandler(
    IMessageRepository messages,
    IFolderRepository folders,
    IAccountRepository accounts,
    IImapSessionFactory imapFactory,
    SyncCoordinator coordinator,
    TimeSpan? lockWait = null) : ICommandHandler<DeleteMessageCommand, DeleteOutcome>
{
    /// <summary>
    /// The default wait for a running sync, after which the cached row is deleted instead (a sync repairs that).
    /// </summary>
    private static readonly TimeSpan DefaultLockWait = TimeSpan.FromSeconds(2);

    /// <inheritdoc />
    public async ValueTask<DeleteOutcome> Handle(DeleteMessageCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var message = await messages.GetAsync(command.AccountId, command.MessageId, cancellationToken)
            ?? throw new InvalidOperationException($"Message {command.MessageId} was not found.");
        var folder = await folders.GetAsync(message.AccountId, message.FolderId, cancellationToken)
            ?? throw new InvalidOperationException($"Folder {message.FolderId} was not found.");
        var account = await accounts.GetAsync(message.AccountId, cancellationToken)
            ?? throw new InvalidOperationException($"Account {message.AccountId} was not found.");

        // Deleting from Trash, or from an account without Trash, is permanent.
        var trash = (await folders.GetByAccountAsync(account.Id, cancellationToken))
            .FirstOrDefault(f => f.Role == FolderRole.Trash);
        var trashFolder = folder.Role == FolderRole.Trash ? null : trash;
        var trashFullName = trashFolder?.FullName;

        uint? trashUid = null;
        await using (var session = await imapFactory.ConnectAsync(account, cancellationToken))
        {
            // UIDs are only meaningful within one UIDVALIDITY epoch; a stale UID could delete a different message.
            if (folder.UidValidity is { } cachedValidity
                && await session.GetUidValidityAsync(folder.FullName, cancellationToken) != cachedValidity)
            {
                throw new InvalidOperationException(
                    $"The UIDs cached for folder '{folder.FullName}' are stale (UIDVALIDITY changed on the server). Synchronize the account first.");
            }

            var newUid = await session.DeleteMessageAsync(folder.FullName, message.Uid, trashFullName, cancellationToken);
            if (newUid is { } moved && trashFolder is not null && await IsTrashContiguousAsync(session, trashFolder, moved, cancellationToken))
            {
                trashUid = moved;
            }
        }

        // Only after the server confirmed: a failure above leaves the local copy untouched.
        if (trashUid is { } uid && trashFolder is not null && await TryRehomeAsync(message, trashFolder, uid, cancellationToken))
        {
            return DeleteOutcome.MovedToTrash;
        }

        await messages.DeleteAsync(message.AccountId, message.Id, cancellationToken);
        return trashFullName is null ? DeleteOutcome.DeletedPermanently : DeleteOutcome.MovedToTrash;
    }

    /// <summary>
    /// Checks that the moved message can join the cached Trash without leaving a hole: the cached Trash belongs to the
    /// server's current UIDVALIDITY and the server holds no message between the highest cached UID and the new one.
    /// A hole would never be filled, because the incremental sync only looks above the highest cached UID.
    /// </summary>
    /// <param name="session">The IMAP session.</param>
    /// <param name="trash">The cached Trash folder.</param>
    /// <param name="newUid">The UID of the moved message in Trash.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> if the message can be added to the cached Trash as it is.</returns>
    private async Task<bool> IsTrashContiguousAsync(IImapSession session, MailFolder trash, uint newUid, CancellationToken cancellationToken)
    {
        if (trash.UidValidity is not { } cachedValidity
            || await session.GetUidValidityAsync(trash.FullName, cancellationToken) != cachedValidity)
        {
            return false;
        }

        var highestCached = await messages.GetMaxUidAsync(trash.AccountId, trash.Id, cancellationToken);
        return newUid > highestCached && !await session.HasMessagesBetweenAsync(trash.FullName, highestCached, newUid, cancellationToken);
    }

    /// <summary>
    /// Moves the cached row into the cached Trash under the lock of the account's syncs.
    /// </summary>
    /// <param name="message">The tracked cached message.</param>
    /// <param name="trash">The cached Trash folder.</param>
    /// <param name="uid">The UID of the message in Trash.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// <see langword="true"/> if the row now lives in Trash; <see langword="false"/> if the caller should delete it instead
    /// (a sync is running for too long, or a sync already cached the message from Trash).
    /// </returns>
    private async Task<bool> TryRehomeAsync(MailMessage message, MailFolder trash, uint uid, CancellationToken cancellationToken)
    {
        using var lockTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lockTimeout.CancelAfter(lockWait ?? DefaultLockWait);
        try
        {
            return await coordinator.RunExclusiveAsync(
                message.AccountId,
                async () =>
                {
                    // A sync that ran between the move and now may have downloaded the message from Trash already.
                    if (await messages.ExistsAsync(message.AccountId, trash.Id, uid, cancellationToken))
                    {
                        return false;
                    }

                    message.MoveToFolder(trash.Id, uid);
                    await messages.SaveChangesAsync(cancellationToken);
                    return true;
                },
                lockTimeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Only the wait for the lock timed out; a running sync is not worth blocking the user for.
            return false;
        }
    }
}
