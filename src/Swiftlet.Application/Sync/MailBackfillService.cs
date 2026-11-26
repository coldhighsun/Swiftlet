using Microsoft.Extensions.Logging;
using Swiftlet.Application.Abstractions;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Application.Sync;

/// <summary>
/// What a backfill batch did.
/// </summary>
public enum BackfillOutcome
{
    /// <summary>
    /// A batch was stored (or skipped over); older messages remain.
    /// </summary>
    Progressed = 0,

    /// <summary>
    /// The folder has no older messages left.
    /// </summary>
    Completed = 1,

    /// <summary>
    /// Nothing can be backfilled yet: the folder has no cached messages, so the regular sync has to download the newest ones first.
    /// </summary>
    NotStarted = 2,

    /// <summary>
    /// The batch was not run because the cache and the server disagree (UIDVALIDITY changed, which the regular sync repairs), the folder is gone or it is being emptied.
    /// </summary>
    Skipped = 3,

    /// <summary>
    /// The server kept reporting older messages without delivering any, so this pass gives up on the folder.
    /// </summary>
    Stalled = 4,
}

/// <summary>
/// The result of one backfill batch.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Added">The number of messages cached by the batch.</param>
/// <param name="Remaining">How many older messages the server still holds after the batch.</param>
/// <param name="Copied">How many of the added messages were copied from another folder instead of downloaded.</param>
public sealed record BackfillBatchResult(BackfillOutcome Outcome, int Added, int Remaining, int Copied = 0);

/// <summary>
/// Downloads the history of a folder in batches, walking from the oldest cached message towards the oldest message on the server.
/// </summary>
/// <param name="folders">The folder repository.</param>
/// <param name="messages">The message repository.</param>
/// <param name="options">The synchronization limits.</param>
/// <param name="settings">The user-configurable sync behavior.</param>
/// <param name="logger">The logger.</param>
public sealed class MailBackfillService(
    IFolderRepository folders,
    IMessageRepository messages,
    MailSyncOptions options,
    ISyncSettings settings,
    ILogger<MailBackfillService> logger)
{
    /// <summary>
    /// How many times a batch whose messages all vanished is retried further back before the folder is given up for this pass.
    /// </summary>
    private const int MaxEmptyPages = 5;

    /// <summary>
    /// Gets the folders of an account whose history should be downloaded, most important first.
    /// A folder that mirrors all other folders (Gmail's "All Mail") comes after the regular folders, when the user allows it, so most of its
    /// messages are already cached from the other folders and are copied instead of downloaded. Trash and Junk come last of all.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The folders in backfill order.</returns>
    public async Task<IReadOnlyList<MailFolder>> GetFoldersToBackfillAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var all = await folders.GetByAccountAsync(accountId, cancellationToken);
        return
        [
            .. all
                .Where(f => f.Role != FolderRole.AllMail || settings.DownloadAllMail)
                .OrderBy(f => FolderSyncOrder.Priority(f.Role))
                .ThenBy(f => f.FullName, StringComparer.Ordinal),
        ];
    }

    /// <summary>
    /// Downloads and stores the next batch of older messages of a folder.
    /// The caller must hold the account's <see cref="SyncCoordinator"/> lock so no regular sync changes the folder meanwhile.
    /// </summary>
    /// <param name="session">The IMAP session.</param>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="folderId">The folder identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The batch result.</returns>
    public Task<BackfillBatchResult> BackfillBatchAsync(
        IImapSession session,
        Guid accountId,
        Guid folderId,
        CancellationToken cancellationToken = default)
    {
        return BackfillBatchAsync(session, accountId, folderId, null, cancellationToken);
    }

    /// <summary>
    /// Downloads and stores the next batch of older messages of a folder, committing every message as soon as it is downloaded.
    /// Messages are stored from the newest to the oldest, so the lowest cached UID (the cursor of the next batch) always moves down without leaving a gap,
    /// and an interrupted batch loses at most the message that was being downloaded.
    /// The caller must hold the account's <see cref="SyncCoordinator"/> lock so no regular sync changes the folder meanwhile.
    /// </summary>
    /// <param name="session">The IMAP session.</param>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="folderId">The folder identifier.</param>
    /// <param name="onStored">Called after messages were committed, with the number stored so far in this batch and how many older messages the folder still holds; optional.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The batch result.</returns>
    public async Task<BackfillBatchResult> BackfillBatchAsync(
        IImapSession session,
        Guid accountId,
        Guid folderId,
        Action<int, int>? onStored,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        await using var connections = new BackfillConnections(session);
        return await BackfillBatchAsync(connections, accountId, folderId, onStored, null, cancellationToken);
    }

    /// <summary>
    /// Downloads and stores the next batch of older messages of a folder over several connections at once.
    /// Downloads overlap, but messages are committed strictly from the newest to the oldest, so the cursor never leaves a gap.
    /// The caller must hold the account's <see cref="SyncCoordinator"/> lock so no regular sync changes the folder meanwhile.
    /// </summary>
    /// <param name="connections">The connections of the pass.</param>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="folderId">The folder identifier.</param>
    /// <param name="onStored">Called after messages were committed, with the number stored so far in this batch and how many older messages the folder still holds; optional.</param>
    /// <param name="shouldYield">
    /// Asks whether the batch should end early because someone is waiting for the account; the downloads already running are still stored
    /// and the rest is left for the next batch. Optional.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The batch result.</returns>
    public async Task<BackfillBatchResult> BackfillBatchAsync(
        BackfillConnections connections,
        Guid accountId,
        Guid folderId,
        Action<int, int>? onStored,
        Func<bool>? shouldYield = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connections);
        var session = connections.Primary;

        var folder = await folders.GetAsync(accountId, folderId, cancellationToken);
        // History of a folder that is being emptied would only be downloaded to be deleted again.
        if (folder is null || folder.EmptyThroughUid is not null)
        {
            return new BackfillBatchResult(BackfillOutcome.Skipped, 0, 0);
        }

        // Cached UIDs are only meaningful for the UIDVALIDITY they were stored under; after a change the regular sync clears them.
        var uidValidity = await session.GetUidValidityAsync(folder.FullName, cancellationToken);
        if (folder.UidValidity != uidValidity)
        {
            return new BackfillBatchResult(BackfillOutcome.Skipped, 0, 0);
        }

        var before = await messages.GetMinUidAsync(accountId, folderId, cancellationToken);
        if (before == 0)
        {
            return new BackfillBatchResult(BackfillOutcome.NotStarted, 0, 0);
        }

        for (var attempt = 0; attempt < MaxEmptyPages; attempt++)
        {
            var page = await session.ListOlderAsync(folder.FullName, before, options.BackfillBatchSize, cancellationToken);
            if (page.Headers.Count == 0)
            {
                return new BackfillBatchResult(BackfillOutcome.Completed, 0, 0);
            }

            // A message the account already holds in another folder (Gmail keeps every message in All Mail too) is copied
            // from the local cache; only messages never seen before are downloaded.
            var wanted = page.Headers.Where(h => h.MessageId is not null).Select(h => h.MessageId!).ToList();
            var known = await messages.FindByMessageIdsAsync(accountId, wanted, folderId, cancellationToken);
            var storedCount = 0;
            var copied = 0;
            var handled = 0;
            var pendingCopies = new List<MailMessage>();

            // Commits the copies collected so far; always done before a download so higher UIDs are stored before lower ones.
            async Task FlushCopiesAsync()
            {
                if (pendingCopies.Count == 0)
                {
                    return;
                }

                await messages.AddRangeAsync(pendingCopies, cancellationToken);
                storedCount += pendingCopies.Count;
                copied += pendingCopies.Count;
                pendingCopies.Clear();
                onStored?.Invoke(storedCount, page.Remaining + page.Headers.Count - handled);
            }

            var ordered = page.Headers.OrderByDescending(h => h.Uid).ToList();
            var missing = ordered.Where(h => h.MessageId is null || !known.ContainsKey(h.MessageId)).ToList();

            // Further connections are only worth opening when there is more than one message to download.
            var helpers = missing.Count > 1 ? await connections.GetHelpersAsync(cancellationToken) : [];
            await using var downloader = new ParallelDownloader(connections, helpers, folder.FullName, missing, logger, cancellationToken, shouldYield);
            var yielded = false;

            foreach (var header in ordered)
            {
                var isKnown = header.MessageId is not null && known.ContainsKey(header.MessageId);

                // Everything above this message is stored or about to be, so ending here leaves no gap.
                if (!isKnown && downloader.IsStopped)
                {
                    yielded = true;
                    break;
                }

                handled++;

                // A message the account already holds in another folder is copied from the local cache; nothing is downloaded.
                if (header.MessageId is not null && known.TryGetValue(header.MessageId, out var original))
                {
                    pendingCopies.Add(original.CopyToFolder(folder.Id, header.Uid, header.IsRead));
                    continue;
                }

                await FlushCopiesAsync();
                var downloaded = await downloader.TakeNextAsync(cancellationToken);
                if (downloaded.Count == 0)
                {
                    continue;
                }

                await messages.AddRangeAsync([MailSyncService.ToMessage(folder, downloaded[0])], cancellationToken);
                storedCount++;
                onStored?.Invoke(storedCount, page.Remaining + page.Headers.Count - handled);
            }

            await FlushCopiesAsync();

            if (yielded)
            {
                // The messages of this page that were not handled are still older messages on the server.
                var left = page.Remaining + page.Headers.Count - handled;
                logger.LogDebug("Backfill of {Folder} stored {Count} messages and made way for other work.", folder.FullName, storedCount);
                return new BackfillBatchResult(BackfillOutcome.Progressed, storedCount, left, copied);
            }

            if (storedCount > 0)
            {
                logger.LogDebug(
                    "Backfilled {Count} messages into {Folder} ({Copied} copied from other folders); {Remaining} older remain.",
                    storedCount,
                    folder.FullName,
                    copied,
                    page.Remaining);
                return new BackfillBatchResult(
                    page.Remaining > 0 ? BackfillOutcome.Progressed : BackfillOutcome.Completed,
                    storedCount,
                    page.Remaining,
                    copied);
            }

            if (page.Remaining == 0 || page.OldestUid == 0)
            {
                return new BackfillBatchResult(BackfillOutcome.Completed, 0, 0);
            }

            // Every message of this batch vanished before it could be downloaded; step over the batch and ask again.
            before = page.OldestUid;
        }

        logger.LogWarning("Backfill of {Folder} found only vanished messages {Pages} times in a row; giving up for now.", folder.FullName, MaxEmptyPages);
        return new BackfillBatchResult(BackfillOutcome.Stalled, 0, 0);
    }
}
