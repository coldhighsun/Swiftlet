using Swiftlet.Application.Abstractions;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Application.Messages;

/// <summary>
/// What one batch of the automatic Trash cleanup did.
/// </summary>
/// <param name="FolderName">The full name of the Trash folder.</param>
/// <param name="Deleted">The number of messages deleted on the server.</param>
/// <param name="Remaining">How many expired messages are still waiting.</param>
public sealed record TrashCleanupResult(string FolderName, int Deleted, int Remaining);

/// <summary>
/// Permanently deletes the messages that have been in an account's Trash folder longer than the account keeps them,
/// including ones that were never downloaded.
/// </summary>
/// <param name="folders">The folder repository.</param>
/// <param name="messages">The message repository.</param>
/// <param name="timeProvider">The clock.</param>
public sealed class TrashCleanupService(IFolderRepository folders, IMessageRepository messages, TimeProvider timeProvider)
{
    /// <summary>
    /// The largest number of messages deleted per batch, so syncs waiting for the account are not blocked for long.
    /// </summary>
    public const int BatchSize = 100;

    /// <summary>
    /// Records what the Trash holds and deletes the next expired messages.
    /// The caller must hold the account's <see cref="Sync.SyncCoordinator"/> lock so no sync changes the folder meanwhile.
    /// A failure is thrown and leaves the cached messages as they are; a later call tries again.
    /// </summary>
    /// <param name="session">The IMAP session.</param>
    /// <param name="account">The account.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The batch result, or <see langword="null"/> when nothing was deleted.</returns>
    /// <exception cref="MailOperationRejectedException">The server refused the operation.</exception>
    /// <exception cref="NotSupportedException">The server lacks a feature needed to delete safely.</exception>
    public async Task<TrashCleanupResult?> ProcessBatchAsync(
        IImapSession session,
        MailAccount account,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(account);

        if (account.TrashRetentionDays is not { } days || account.TrashCleanupEnabledAt is not { } enabledAt)
        {
            return null;
        }

        // The same folder a deletion moves messages to.
        var trash = (await folders.GetByAccountAsync(account.Id, cancellationToken))
            .FirstOrDefault(f => f.Role == FolderRole.Trash);

        // UIDs are only meaningful within one UIDVALIDITY epoch; after a change the next sync resets the folder first.
        if (trash?.UidValidity is not { } cachedValidity
            || await session.GetUidValidityAsync(trash.FullName, cancellationToken) != cachedValidity)
        {
            return null;
        }

        var onServer = await session.ListUidsBetweenAsync(trash.FullName, 0, uint.MaxValue, cancellationToken);
        var through = trash.ObserveTrash(
            onServer.Count == 0 ? 0 : onServer[^1],
            timeProvider.GetUtcNow(),
            enabledAt,
            TimeSpan.FromDays(days));
        await folders.SaveChangesAsync(cancellationToken);

        // A message the user is restoring or deleting is left to the background deletion.
        var leaving = (await messages.GetPendingUidsAsync(account.Id, trash.Id, cancellationToken)).ToHashSet();
        var expired = onServer.Where(u => u <= through && !leaving.Contains(u)).ToList();
        if (expired.Count == 0)
        {
            return null;
        }

        var batch = expired.Take(BatchSize).ToList();
        await session.DeleteMessagesAsync(trash.FullName, batch, null, cancellationToken);

        // Only after the server confirmed.
        var cached = await messages.GetByUidsAsync(account.Id, trash.Id, batch, cancellationToken);
        await messages.DeleteRangeAsync(account.Id, [.. cached.Select(c => c.Id)], cancellationToken);
        return new TrashCleanupResult(trash.FullName, batch.Count, expired.Count - batch.Count);
    }
}
