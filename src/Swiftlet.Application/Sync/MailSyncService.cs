using Microsoft.Extensions.Logging;
using Swiftlet.Application.Abstractions;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Application.Sync;

/// <summary>
/// The outcome of synchronizing one account.
/// </summary>
/// <param name="FoldersSynced">The number of folders synchronized successfully.</param>
/// <param name="NewMessages">The number of new messages cached.</param>
/// <param name="FailedFolders">The full names of folders that failed to synchronize.</param>
public sealed record SyncResult(int FoldersSynced, int NewMessages, IReadOnlyList<string> FailedFolders);

/// <summary>
/// Tunable limits of the synchronization.
/// </summary>
/// <param name="MaxMessagesPerFolderPerSync">The maximum number of messages downloaded per folder per run.</param>
public sealed record MailSyncOptions(int MaxMessagesPerFolderPerSync = 200);

/// <summary>
/// Synchronizes folders and new messages of an account from IMAP into the local cache.
/// </summary>
/// <param name="accounts">The account repository.</param>
/// <param name="folders">The folder repository.</param>
/// <param name="messages">The message repository.</param>
/// <param name="imapFactory">The IMAP connection factory.</param>
/// <param name="settings">The user-configurable sync behavior, read on every run.</param>
/// <param name="options">The synchronization limits.</param>
/// <param name="logger">The logger.</param>
public sealed class MailSyncService(
    IAccountRepository accounts,
    IFolderRepository folders,
    IMessageRepository messages,
    IImapSessionFactory imapFactory,
    ISyncSettings settings,
    MailSyncOptions options,
    ILogger<MailSyncService> logger)
{
    /// <summary>
    /// Synchronizes an account.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The synchronization result.</returns>
    public async Task<SyncResult> SyncAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var account = await accounts.GetAsync(accountId, cancellationToken)
            ?? throw new InvalidOperationException($"Account {accountId} was not found.");

        await using var session = await imapFactory.ConnectAsync(account, cancellationToken);

        var remoteFolders = await session.ListFoldersAsync(cancellationToken);
        var localFolders = await EnsureFoldersAsync(account, remoteFolders, cancellationToken);

        var synced = 0;
        var newMessages = 0;
        var failed = new List<string>();
        foreach (var folder in localFolders)
        {
            try
            {
                newMessages += await SyncFolderAsync(session, folder, cancellationToken);
                synced++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One broken folder must not prevent the remaining folders from synchronizing.
                logger.LogWarning(ex, "Failed to synchronize folder {Folder} of account {Account}.", folder.FullName, account.EmailAddress);
                failed.Add(folder.FullName);
            }
        }

        logger.LogInformation(
            "Synchronized account {Account}: {Folders} folders, {Messages} new messages, {Failed} failed.",
            account.EmailAddress,
            synced,
            newMessages,
            failed.Count);
        return new SyncResult(synced, newMessages, failed);
    }

    /// <summary>
    /// Adds locally any selectable remote folder that is not yet known and returns the folders to synchronize.
    /// </summary>
    /// <param name="account">The account.</param>
    /// <param name="remoteFolders">The folders reported by the server.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The local folders that still exist on the server and can be selected.</returns>
    private async Task<IReadOnlyList<MailFolder>> EnsureFoldersAsync(
        MailAccount account,
        IReadOnlyList<RemoteFolder> remoteFolders,
        CancellationToken cancellationToken)
    {
        var local = (await folders.GetByAccountAsync(account.Id, cancellationToken)).ToList();
        var known = local.Select(f => f.FullName).ToHashSet(StringComparer.Ordinal);
        foreach (var remote in remoteFolders.Where(r => r.CanSelect && !known.Contains(r.FullName)))
        {
            var folder = MailFolder.Create(account.Id, remote.FullName, remote.Name, remote.Role);
            await folders.AddAsync(folder, cancellationToken);
            local.Add(folder);
        }

        var selectable = remoteFolders.Where(r => r.CanSelect).Select(r => r.FullName).ToHashSet(StringComparer.Ordinal);
        return local.Where(f => selectable.Contains(f.FullName)).ToList();
    }

    /// <summary>
    /// Synchronizes one folder.
    /// </summary>
    /// <param name="session">The IMAP session.</param>
    /// <param name="folder">The folder.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of new messages cached.</returns>
    private async Task<int> SyncFolderAsync(IImapSession session, MailFolder folder, CancellationToken cancellationToken)
    {
        var uidValidity = await session.GetUidValidityAsync(folder.FullName, cancellationToken);
        var invalidated = folder.UpdateUidValidity(uidValidity);
        await folders.SaveChangesAsync(cancellationToken);
        if (invalidated)
        {
            logger.LogInformation("UIDVALIDITY of {Folder} changed; discarding cached messages.", folder.FullName);
            await messages.DeleteByFolderAsync(folder.AccountId, folder.Id, cancellationToken);
        }
        else
        {
            // Local flag changes go up before anything is pulled, so a pull never reverts them.
            await PushPendingFlagsAsync(session, folder, cancellationToken);
        }

        var afterUid = await messages.GetMaxUidAsync(folder.AccountId, folder.Id, cancellationToken);
        var remote = await session.FetchNewMessagesAsync(
            folder.FullName,
            afterUid,
            options.MaxMessagesPerFolderPerSync,
            cancellationToken);
        var created = remote.Select(r => ToMessage(folder, r)).ToList();
        if (created.Count > 0)
        {
            await messages.AddRangeAsync(created, cancellationToken);
        }

        if (!invalidated)
        {
            await PullFlagsAsync(session, folder, cancellationToken);
        }

        return created.Count;
    }

    /// <summary>
    /// Writes the read and starred state of locally changed messages to the server.
    /// </summary>
    /// <param name="session">The IMAP session.</param>
    /// <param name="folder">The folder.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the pending changes are pushed.</returns>
    private async Task PushPendingFlagsAsync(IImapSession session, MailFolder folder, CancellationToken cancellationToken)
    {
        var pending = await messages.GetPendingFlagStatesAsync(folder.AccountId, folder.Id, cancellationToken);
        if (pending.Count == 0)
        {
            return;
        }

        var updates = pending.Select(m => new FlagUpdate(m.Uid, m.IsRead, m.IsFlagged)).ToList();

        try
        {
            await session.SetFlagsAsync(folder.FullName, updates, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed push is recoverable: the messages stay pending and are retried at the next sync, and the pending
            // marker also protects them from the pull. A persistently failing STORE (read-only mailbox, permissions)
            // must not stop new mail from being received for this folder.
            logger.LogWarning(
                ex,
                "Failed to push flags of {Count} messages in {Folder}; they stay pending.",
                pending.Count,
                folder.FullName);
            return;
        }

        // The sync runs on its own scope, so the user may change a message while the push was in flight. The marker is
        // therefore cleared in the database only where the stored values still equal what was pushed; a message the user
        // changed meanwhile stays pending and its newer value goes up at the next sync.
        var cleared = await messages.ClearFlagSyncPendingAsync(folder.AccountId, pending, cancellationToken);
        logger.LogInformation("Pushed flags of {Count} messages in {Folder}.", cleared, folder.FullName);
    }

    /// <summary>
    /// Copies server-side flags onto cached messages and optionally removes messages deleted on the server.
    /// </summary>
    /// <param name="session">The IMAP session.</param>
    /// <param name="folder">The folder.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the pull is applied.</returns>
    private async Task PullFlagsAsync(IImapSession session, MailFolder folder, CancellationToken cancellationToken)
    {
        var cached = await messages.GetFlagStatesAsync(folder.AccountId, folder.Id, cancellationToken);
        if (cached.Count == 0)
        {
            return;
        }

        var remote = await session.FetchFlagsAsync(folder.FullName, cached[0].Uid, cancellationToken);
        var remoteByUid = remote.ToDictionary(r => r.Uid);
        var gone = new List<Guid>();
        foreach (var message in cached)
        {
            if (remoteByUid.TryGetValue(message.Uid, out var state) && !state.IsDeleted)
            {
                // The repository skips pending rows in the statement itself, so a change the user made after the
                // snapshot was taken is not overwritten either.
                if (!message.IsFlagSyncPending && (message.IsRead != state.IsRead || message.IsFlagged != state.IsFlagged))
                {
                    await messages.ApplyServerFlagsAsync(folder.AccountId, message.Id, state.IsRead, state.IsFlagged, cancellationToken);
                }
            }
            else
            {
                gone.Add(message.Id);
            }
        }

        if (gone.Count == 0 || !settings.RemoveServerDeletedMessages)
        {
            return;
        }

        if (remote.Count == 0)
        {
            // An empty answer for a non-empty cache is more likely a server hiccup than a wiped mailbox.
            logger.LogWarning(
                "Server returned no flags for {Folder} although {Count} messages are cached; removing nothing.",
                folder.FullName,
                cached.Count);
            return;
        }

        await messages.DeleteRangeAsync(folder.AccountId, gone, cancellationToken);
        logger.LogInformation("Removed {Count} messages deleted on the server from {Folder}.", gone.Count, folder.FullName);
    }

    /// <summary>
    /// Converts a downloaded message into a cache entity.
    /// </summary>
    /// <param name="folder">The containing folder.</param>
    /// <param name="remote">The downloaded message.</param>
    /// <returns>The entity.</returns>
    private static MailMessage ToMessage(MailFolder folder, RemoteMessage remote)
    {
        var message = MailMessage.Create(
            folder.AccountId,
            folder.Id,
            remote.Uid,
            remote.Subject,
            remote.FromName,
            remote.FromAddress,
            remote.Recipients,
            remote.SentAt,
            remote.BodyText,
            remote.BodyHtml,
            remote.MessageId,
            remote.IsRead);
        foreach (var attachment in remote.Attachments)
        {
            message.AddAttachment(attachment.FileName, attachment.ContentType, attachment.Size);
        }

        return message;
    }
}
