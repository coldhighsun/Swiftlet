using Microsoft.Extensions.Logging;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Logging;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Application.Messages;

/// <summary>
/// What one batch of pending deletions or moves did.
/// </summary>
/// <param name="FolderName">The full name of the folder worked on.</param>
/// <param name="Deleted">The number of messages deleted or moved on the server.</param>
/// <param name="Remaining">How many messages are still known to be waiting.</param>
/// <param name="Error">Why the server refused, in which case the refused messages are visible again; otherwise <see langword="null"/>.</param>
public sealed record DeletionBatchResult(string FolderName, int Deleted, int Remaining, string? Error = null);

/// <summary>
/// Carries out pending deletions and moves on the server in batches.
/// When the server moved messages and the cached destination folder is provably complete, the cached rows are re-homed
/// into it instead of being deleted, so the next sync does not download the same messages again.
/// </summary>
/// <param name="folders">The folder repository.</param>
/// <param name="messages">The message repository.</param>
/// <param name="logger">The logger.</param>
/// <param name="names">Resolves the names shown in the log; optional.</param>
public sealed class PendingDeletionService(
    IFolderRepository folders,
    IMessageRepository messages,
    ILogger<PendingDeletionService> logger,
    LogNames? names = null)
{
    /// <summary>
    /// The names shown in the log; identifiers when no directory is supplied.
    /// </summary>
    private readonly LogNames _names = names ?? new LogNames();

    /// <summary>
    /// The largest number of messages deleted per batch, so syncs waiting for the account are not blocked for long.
    /// </summary>
    public const int BatchSize = 100;

    /// <summary>
    /// Determines whether an account has anything left to delete or move.
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
    /// Deletes or moves the next batch of one folder on the server and updates the cache once the server confirmed.
    /// All messages of a batch share one destination.
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
        Guid? targetId = null;
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
            var batchTargetId = pending[0].TargetFolderId;
            pending = [.. pending.Where(p => p.FolderId == folderId && p.TargetFolderId == batchTargetId)];
            targetId = batchTargetId;
        }

        using var logScope = logger.BeginMailScope(_names.Account(accountId), folder.FullName);
        try
        {
            // UIDs are only meaningful within one UIDVALIDITY epoch; a stale UID could delete a different message.
            if (folder.UidValidity is { } cachedValidity
                && await session.GetUidValidityAsync(folder.FullName, cancellationToken) != cachedValidity)
            {
                throw new MailOperationRejectedException(
                    $"The UIDs cached for folder '{folder.FullName}' are stale (UIDVALIDITY changed on the server). Synchronize the account first.");
            }

            MailFolder? destination;
            if (targetId is { } id)
            {
                destination = all.FirstOrDefault(f => f.Id == id)
                    ?? throw new MailOperationRejectedException("The folder the messages were to be moved to no longer exists.");
            }
            else
            {
                // Deleting from Trash, or from an account without Trash, is permanent.
                destination = folder.Role == FolderRole.Trash ? null : all.FirstOrDefault(f => f.Role == FolderRole.Trash);
            }

            return folder.EmptyThroughUid is { } through
                ? await EmptyBatchAsync(session, folder, destination, through, cancellationToken)
                : await DeleteBatchAsync(session, folder, destination, pending, cancellationToken);
        }
        catch (Exception ex) when (ex is MailOperationRejectedException or NotSupportedException)
        {
            // Repeating cannot help, so the messages come back into view and the user is told why.
            logger.LogWarning(ex, "The server refused to delete or move messages of {Folder}; they are shown again.", folder.FullName);
            folder.ClearEmptyRequest();
            await folders.SaveChangesAsync(cancellationToken);
            await messages.ClearDeletePendingAsync(accountId, folder.Id, targetId, cancellationToken);
            return new DeletionBatchResult(
                folder.FullName,
                0,
                await messages.CountPendingDeletionsAsync(accountId, cancellationToken),
                ex.Message);
        }
    }

    /// <summary>
    /// Deletes or moves selected messages of a folder.
    /// </summary>
    /// <param name="session">The IMAP session.</param>
    /// <param name="folder">The folder.</param>
    /// <param name="destination">The folder to move the messages to, or <see langword="null"/> to delete permanently.</param>
    /// <param name="batch">The messages.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The batch result.</returns>
    private async Task<DeletionBatchResult> DeleteBatchAsync(
        IImapSession session,
        MailFolder folder,
        MailFolder? destination,
        IReadOnlyList<PendingDeletion> batch,
        CancellationToken cancellationToken)
    {
        var moved = await session.DeleteMessagesAsync(folder.FullName, [.. batch.Select(p => p.Uid)], destination?.FullName, cancellationToken);

        // Only after the server confirmed: a failure above leaves the local rows pending.
        await RemoveCachedAsync(session, folder.AccountId, destination, batch, moved, cancellationToken);
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
    /// Updates the cache for messages the server removed from a folder: rows that can join the cached destination without a
    /// hole are moved there, the others are removed and the next sync downloads them from the destination.
    /// </summary>
    /// <param name="session">The IMAP session.</param>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="destination">The folder the messages were moved to, or <see langword="null"/> after a permanent delete.</param>
    /// <param name="cached">The cached rows of the removed messages.</param>
    /// <param name="moved">The new UID of each moved message, keyed by its old UID.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the cache is up to date.</returns>
    private async Task RemoveCachedAsync(
        IImapSession session,
        Guid accountId,
        MailFolder? destination,
        IReadOnlyList<PendingDeletion> cached,
        IReadOnlyDictionary<uint, uint> moved,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<(Guid MessageId, uint Uid)> rehomed = [];
        if (destination is not null)
        {
            try
            {
                rehomed = await FindRehomableAsync(session, destination, cached, moved, cancellationToken);
            }
            catch (MailOperationRejectedException ex)
            {
                // The messages are already moved; only the shortcut is lost, and the next sync downloads them from there.
                logger.LogWarning(ex, "Could not inspect {Folder}; the moved messages will be downloaded again.", destination.FullName);
            }

            await messages.MoveToFolderAsync(accountId, destination.Id, [.. rehomed], cancellationToken);
        }

        var kept = rehomed.Select(r => r.MessageId).ToHashSet();
        await messages.DeleteRangeAsync(accountId, [.. cached.Select(c => c.Id).Where(id => !kept.Contains(id))], cancellationToken);
    }

    /// <summary>
    /// Finds the moved rows that can join the cached destination: it belongs to the server's current UIDVALIDITY and
    /// the server holds no other message between the highest cached UID and theirs. A hole would never be filled, because
    /// the incremental sync only looks above the highest cached UID.
    /// </summary>
    /// <param name="session">The IMAP session.</param>
    /// <param name="destination">The cached destination folder.</param>
    /// <param name="cached">The cached rows of the moved messages.</param>
    /// <param name="moved">The new UID of each moved message, keyed by its old UID.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The rows to move with their new UIDs.</returns>
    private async Task<IReadOnlyList<(Guid MessageId, uint Uid)>> FindRehomableAsync(
        IImapSession session,
        MailFolder destination,
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
            || destination.UidValidity is not { } cachedValidity
            || await session.GetUidValidityAsync(destination.FullName, cancellationToken) != cachedValidity)
        {
            return [];
        }

        var highestCached = await messages.GetMaxUidAsync(destination.AccountId, destination.Id, cancellationToken);
        candidates = [.. candidates.Where(c => c.Uid > highestCached)];
        if (candidates.Count == 0)
        {
            return [];
        }

        var newest = candidates.Max(c => c.Uid);
        var onServer = await session.ListUidsBetweenAsync(
            destination.FullName,
            highestCached,
            newest == uint.MaxValue ? newest : newest + 1,
            cancellationToken);
        var ours = candidates.Select(c => c.Uid).ToHashSet();
        var firstHole = onServer.Where(u => !ours.Contains(u)).DefaultIfEmpty(uint.MaxValue).Min();
        return [.. candidates.Where(c => c.Uid < firstHole)];
    }
}
