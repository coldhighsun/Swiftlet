using Microsoft.Extensions.Logging;
using Swiftlet.Application.Abstractions;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Application.Messages;

/// <summary>
/// What one deletion batch did.
/// </summary>
/// <param name="FolderName">The full name of the folder worked on.</param>
/// <param name="Deleted">The number of messages deleted on the server.</param>
/// <param name="Remaining">How many messages are still known to be waiting for deletion.</param>
/// <param name="Error">Why the server refused, in which case the folder's messages are visible again; otherwise <see langword="null"/>.</param>
public sealed record DeletionBatchResult(string FolderName, int Deleted, int Remaining, string? Error = null);

/// <summary>
/// Carries out pending deletions on the server in batches.
/// When the server moved messages to Trash and the cached Trash is provably complete, the cached rows are re-homed into
/// Trash instead of being deleted, so the next sync does not download the same messages again.
/// </summary>
/// <param name="folders">The folder repository.</param>
/// <param name="messages">The message repository.</param>
/// <param name="logger">The logger.</param>
public sealed class PendingDeletionService(
    IFolderRepository folders,
    IMessageRepository messages,
    ILogger<PendingDeletionService> logger)
{
    /// <summary>
    /// The largest number of messages deleted per batch, so syncs waiting for the account are not blocked for long.
    /// </summary>
    public const int BatchSize = 100;

    /// <summary>
    /// Determines whether an account has anything left to delete.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> if a batch would do something.</returns>
    public async Task<bool> HasWorkAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        return (await folders.GetByAccountAsync(accountId, cancellationToken)).Any(f => f.EmptyThroughUid is not null)
            || await messages.CountPendingDeletionsAsync(accountId, cancellationToken) > 0;
    }

    /// <summary>
    /// Deletes the next batch of one folder on the server and updates the cache once the server confirmed.
    /// The caller must hold the account's <see cref="Sync.SyncCoordinator"/> lock so no sync changes the folders meanwhile.
    /// A failure that may pass (such as a lost connection) is thrown and leaves everything pending.
    /// </summary>
    /// <param name="session">The IMAP session.</param>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The batch result, or <see langword="null"/> when nothing is pending.</returns>
    public async Task<DeletionBatchResult?> ProcessBatchAsync(IImapSession session, Guid accountId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var all = await folders.GetByAccountAsync(accountId, cancellationToken);
        var folder = all.FirstOrDefault(f => f.EmptyThroughUid is not null);
        IReadOnlyList<PendingDeletion> pending = [];
        if (folder is null)
        {
            pending = await messages.GetPendingDeletionsAsync(accountId, BatchSize, cancellationToken);
            if (pending.Count == 0)
            {
                return null;
            }

            var folderId = pending[0].FolderId;
            folder = all.FirstOrDefault(f => f.Id == folderId)
                ?? throw new InvalidOperationException($"Folder {folderId} was not found.");
            pending = [.. pending.Where(p => p.FolderId == folderId)];
        }

        try
        {
            // UIDs are only meaningful within one UIDVALIDITY epoch; a stale UID could delete a different message.
            if (folder.UidValidity is { } cachedValidity
                && await session.GetUidValidityAsync(folder.FullName, cancellationToken) != cachedValidity)
            {
                throw new MailOperationRejectedException(
                    $"The UIDs cached for folder '{folder.FullName}' are stale (UIDVALIDITY changed on the server). Synchronize the account first.");
            }

            // Deleting from Trash, or from an account without Trash, is permanent.
            var trash = folder.Role == FolderRole.Trash ? null : all.FirstOrDefault(f => f.Role == FolderRole.Trash);
            return folder.EmptyThroughUid is { } through
                ? await EmptyBatchAsync(session, folder, trash, through, cancellationToken)
                : await DeleteBatchAsync(session, folder, trash, pending, cancellationToken);
        }
        catch (Exception ex) when (ex is MailOperationRejectedException or NotSupportedException)
        {
            // Repeating cannot help, so the messages come back into view and the user is told why.
            logger.LogWarning(ex, "The server refused to delete messages of {Folder}; they are shown again.", folder.FullName);
            folder.ClearEmptyRequest();
            await folders.SaveChangesAsync(cancellationToken);
            await messages.ClearDeletePendingAsync(accountId, folder.Id, cancellationToken);
            return new DeletionBatchResult(
                folder.FullName,
                0,
                await messages.CountPendingDeletionsAsync(accountId, cancellationToken),
                ex.Message);
        }
    }

    /// <summary>
    /// Deletes selected messages of a folder.
    /// </summary>
    /// <param name="session">The IMAP session.</param>
    /// <param name="folder">The folder.</param>
    /// <param name="trash">The Trash folder, or <see langword="null"/> to delete permanently.</param>
    /// <param name="batch">The messages.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The batch result.</returns>
    private async Task<DeletionBatchResult> DeleteBatchAsync(
        IImapSession session,
        MailFolder folder,
        MailFolder? trash,
        IReadOnlyList<PendingDeletion> batch,
        CancellationToken cancellationToken)
    {
        var moved = await session.DeleteMessagesAsync(folder.FullName, [.. batch.Select(p => p.Uid)], trash?.FullName, cancellationToken);

        // Only after the server confirmed: a failure above leaves the local rows pending.
        await RemoveCachedAsync(session, folder.AccountId, trash, batch, moved, cancellationToken);
        var remaining = await messages.CountPendingDeletionsAsync(folder.AccountId, cancellationToken);
        return new DeletionBatchResult(folder.FullName, batch.Count, remaining);
    }

    /// <summary>
    /// Deletes the next messages of a folder that is being emptied. The server is walked from the oldest message up, so the
    /// highest cached UID stays in place until the end and no sync in between downloads a message that is about to be deleted.
    /// </summary>
    /// <param name="session">The IMAP session.</param>
    /// <param name="folder">The folder.</param>
    /// <param name="trash">The Trash folder, or <see langword="null"/> to delete permanently.</param>
    /// <param name="through">The highest UID to delete.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The batch result.</returns>
    private async Task<DeletionBatchResult> EmptyBatchAsync(
        IImapSession session,
        MailFolder folder,
        MailFolder? trash,
        uint through,
        CancellationToken cancellationToken)
    {
        var end = through == uint.MaxValue ? through : through + 1;
        var onServer = await session.ListUidsBetweenAsync(folder.FullName, 0, end, cancellationToken);
        var uids = onServer.Take(BatchSize).ToList();
        if (uids.Count > 0)
        {
            var moved = await session.DeleteMessagesAsync(folder.FullName, uids, trash?.FullName, cancellationToken);
            var cached = await messages.GetByUidsAsync(folder.AccountId, folder.Id, uids, cancellationToken);
            await RemoveCachedAsync(session, folder.AccountId, trash, cached, moved, cancellationToken);
        }

        var left = onServer.Count - uids.Count;
        if (left == 0)
        {
            // Rows the server no longer holds go too; then the request is complete.
            await messages.DeleteThroughUidAsync(folder.AccountId, folder.Id, through, cancellationToken);
            folder.ClearEmptyRequest();
            await folders.SaveChangesAsync(cancellationToken);
            left = await messages.CountPendingDeletionsAsync(folder.AccountId, cancellationToken);
        }

        return new DeletionBatchResult(folder.FullName, uids.Count, left);
    }

    /// <summary>
    /// Updates the cache for messages the server deleted: rows that can join the cached Trash without a hole are moved there,
    /// the others are removed and the next sync downloads them from Trash.
    /// </summary>
    /// <param name="session">The IMAP session.</param>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="trash">The Trash folder, or <see langword="null"/> after a permanent delete.</param>
    /// <param name="cached">The cached rows of the deleted messages.</param>
    /// <param name="moved">The new UID of each moved message, keyed by its old UID.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the cache is up to date.</returns>
    private async Task RemoveCachedAsync(
        IImapSession session,
        Guid accountId,
        MailFolder? trash,
        IReadOnlyList<PendingDeletion> cached,
        IReadOnlyDictionary<uint, uint> moved,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<(Guid MessageId, uint Uid)> rehomed = [];
        if (trash is not null)
        {
            try
            {
                rehomed = await FindRehomableAsync(session, trash, cached, moved, cancellationToken);
            }
            catch (MailOperationRejectedException ex)
            {
                // The messages are already moved; only the shortcut is lost, and the next sync downloads them from Trash.
                logger.LogWarning(ex, "Could not inspect {Folder}; the moved messages will be downloaded again.", trash.FullName);
            }

            await messages.MoveToFolderAsync(accountId, trash.Id, [.. rehomed], cancellationToken);
        }

        var kept = rehomed.Select(r => r.MessageId).ToHashSet();
        await messages.DeleteRangeAsync(accountId, [.. cached.Select(c => c.Id).Where(id => !kept.Contains(id))], cancellationToken);
    }

    /// <summary>
    /// Finds the moved rows that can join the cached Trash: the cached Trash belongs to the server's current UIDVALIDITY and
    /// the server holds no other message between the highest cached UID and theirs. A hole would never be filled, because
    /// the incremental sync only looks above the highest cached UID.
    /// </summary>
    /// <param name="session">The IMAP session.</param>
    /// <param name="trash">The cached Trash folder.</param>
    /// <param name="cached">The cached rows of the moved messages.</param>
    /// <param name="moved">The new UID of each moved message, keyed by its old UID.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The rows to move with their new UIDs.</returns>
    private async Task<IReadOnlyList<(Guid MessageId, uint Uid)>> FindRehomableAsync(
        IImapSession session,
        MailFolder trash,
        IReadOnlyList<PendingDeletion> cached,
        IReadOnlyDictionary<uint, uint> moved,
        CancellationToken cancellationToken)
    {
        var candidates = new List<(Guid MessageId, uint Uid)>();
        foreach (var row in cached)
        {
            if (moved.TryGetValue(row.Uid, out var newUid))
            {
                candidates.Add((row.Id, newUid));
            }
        }

        if (candidates.Count == 0
            || trash.UidValidity is not { } cachedValidity
            || await session.GetUidValidityAsync(trash.FullName, cancellationToken) != cachedValidity)
        {
            return [];
        }

        var highestCached = await messages.GetMaxUidAsync(trash.AccountId, trash.Id, cancellationToken);
        candidates = [.. candidates.Where(c => c.Uid > highestCached)];
        if (candidates.Count == 0)
        {
            return [];
        }

        var newest = candidates.Max(c => c.Uid);
        var onServer = await session.ListUidsBetweenAsync(
            trash.FullName,
            highestCached,
            newest == uint.MaxValue ? newest : newest + 1,
            cancellationToken);
        var ours = candidates.Select(c => c.Uid).ToHashSet();
        var firstHole = onServer.Where(u => !ours.Contains(u)).DefaultIfEmpty(uint.MaxValue).Min();
        return [.. candidates.Where(c => c.Uid < firstHole)];
    }
}
