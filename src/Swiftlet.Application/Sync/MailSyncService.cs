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
/// <param name="options">The synchronization limits.</param>
/// <param name="logger">The logger.</param>
public sealed class MailSyncService(
    IAccountRepository accounts,
    IFolderRepository folders,
    IMessageRepository messages,
    IImapSessionFactory imapFactory,
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
            await messages.DeleteByFolderAsync(folder.Id, cancellationToken);
        }

        var afterUid = await messages.GetMaxUidAsync(folder.Id, cancellationToken);
        var remote = await session.FetchNewMessagesAsync(
            folder.FullName,
            afterUid,
            options.MaxMessagesPerFolderPerSync,
            cancellationToken);
        if (remote.Count == 0)
        {
            return 0;
        }

        var created = remote.Select(r => ToMessage(folder, r)).ToList();
        await messages.AddRangeAsync(created, cancellationToken);
        return created.Count;
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
