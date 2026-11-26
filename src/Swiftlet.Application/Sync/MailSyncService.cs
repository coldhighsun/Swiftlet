using System.Collections.Concurrent;
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
/// <param name="InitialFetchCount">The number of newest messages downloaded when a folder has no cached messages; the rest is backfilled.</param>
/// <param name="BackfillBatchSize">The number of older messages downloaded per backfill batch.</param>
/// <param name="ConnectionsPerAccount">The most IMAP connections one sync, and separately one backfill pass, uses for an account; 1 works serially.</param>
public sealed record MailSyncOptions(
    int MaxMessagesPerFolderPerSync = 200,
    int InitialFetchCount = 20,
    int BackfillBatchSize = 100,
    int ConnectionsPerAccount = 3);

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
/// <param name="flags">Serializes the flag work of a folder with the immediate flag push; optional.</param>
public sealed class MailSyncService(
    IAccountRepository accounts,
    IFolderRepository folders,
    IMessageRepository messages,
    IImapSessionFactory imapFactory,
    ISyncSettings settings,
    MailSyncOptions options,
    ILogger<MailSyncService> logger,
    FlagCoordinator? flags = null)
{
    /// <summary>
    /// The flag locks; a private set when no other flag writer exists.
    /// </summary>
    private readonly FlagCoordinator _flags = flags ?? new FlagCoordinator();

    /// <summary>
    /// Synchronizes an account.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The synchronization result.</returns>
    public Task<SyncResult> SyncAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        return SyncAsync(accountId, null, cancellationToken);
    }

    /// <summary>
    /// Synchronizes an account, reporting the INBOX as soon as it is done so new mail can be shown while the other folders are still synchronizing.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="onInboxChanged">
    /// Called with the number of new INBOX messages when the INBOX is done, its cache changed and the account has further folders; optional.
    /// It may be called on any thread and must not throw.
    /// </param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The synchronization result.</returns>
    public async Task<SyncResult> SyncAsync(Guid accountId, Action<int>? onInboxChanged, CancellationToken cancellationToken = default)
    {
        var account = await accounts.GetAsync(accountId, cancellationToken)
            ?? throw new InvalidOperationException($"Account {accountId} was not found.");

        await using var session = await imapFactory.ConnectAsync(account, cancellationToken);

        var remoteFolders = await session.ListFoldersAsync(cancellationToken);
        var localFolders = await EnsureFoldersAsync(account, remoteFolders, cancellationToken);

        using var run = new SyncRun(
            account,
            localFolders.OrderBy(f => FolderSyncOrder.Priority(f.Role)).ThenBy(f => f.FullName, StringComparer.Ordinal),
            localFolders.Count > 1 ? onInboxChanged : null);
        await SyncFoldersAsync(session, run, cancellationToken);

        logger.LogInformation(
            "Synchronized account {Account}: {Folders} folders, {Messages} new messages, {Failed} failed.",
            account.EmailAddress,
            run.Synced,
            run.NewMessages,
            run.Failed.Count);
        return new SyncResult(run.Synced, run.NewMessages, run.Failed);
    }

    /// <summary>
    /// Synchronizes the queued folders on the primary connection, helped by additional connections where there are several folders.
    /// </summary>
    /// <param name="primary">The primary IMAP session.</param>
    /// <param name="run">The state shared by the workers.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when every folder has been attempted and every additional connection is closed.</returns>
    private async Task SyncFoldersAsync(IImapSession primary, SyncRun run, CancellationToken cancellationToken)
    {
        var helperCount = Math.Max(0, Math.Min(options.ConnectionsPerAccount - 1, run.Queue.Count - 1));
        using var stopConnecting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // The primary connection starts at once, so waiting for further connections never delays a sync.
        var primaryWork = ProcessQueueAsync(primary, run, false, cancellationToken);
        var helpers = Enumerable.Range(0, helperCount)
            .Select(_ => RunHelperAsync(run, stopConnecting.Token, cancellationToken))
            .ToList();
        try
        {
            await primaryWork;
        }
        finally
        {
            // The queue is drained (or the sync was cancelled): connections still being opened are no longer needed.
            // Waiting for the helpers keeps all folder work inside the caller's account lock and DI scope.
            await stopConnecting.CancelAsync();
            await Task.WhenAll(helpers);
        }

        while (run.Retry.TryDequeue(out var folder))
        {
            await TrySyncFolderAsync(primary, run, folder, false, cancellationToken);
        }
    }

    /// <summary>
    /// Opens an additional connection and lets it work on the queue; failing to open it is not an error of the sync.
    /// </summary>
    /// <param name="run">The state shared by the workers.</param>
    /// <param name="connectToken">Cancelled when the connection is no longer needed.</param>
    /// <param name="cancellationToken">The cancellation token of the sync.</param>
    /// <returns>A task that completes when the helper has stopped and its connection is closed.</returns>
    private async Task RunHelperAsync(SyncRun run, CancellationToken connectToken, CancellationToken cancellationToken)
    {
        IImapSession session;
        try
        {
            session = await imapFactory.ConnectAsync(run.Account, connectToken);
        }
        catch (OperationCanceledException) when (connectToken.IsCancellationRequested)
        {
            // The other connections finished the queue first, or the whole sync was cancelled (which the primary reports).
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Servers limit the connections per account; the sync works with the connections it has.
            logger.LogWarning(ex, "An additional connection for account {Account} could not be opened; continuing with fewer.", run.Account.EmailAddress);
            return;
        }

        await using (session)
        {
            await ProcessQueueAsync(session, run, true, cancellationToken);
        }
    }

    /// <summary>
    /// Synchronizes queued folders on one connection until the queue is empty. A helper stops after its first failure.
    /// </summary>
    /// <param name="session">The IMAP session.</param>
    /// <param name="run">The state shared by the workers.</param>
    /// <param name="isHelper">Whether the session is an additional connection.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the worker stops.</returns>
    private async Task ProcessQueueAsync(IImapSession session, SyncRun run, bool isHelper, CancellationToken cancellationToken)
    {
        while (run.Queue.TryDequeue(out var folder))
        {
            if (!await TrySyncFolderAsync(session, run, folder, isHelper, cancellationToken) && isHelper)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Synchronizes one folder and records the outcome. A folder that failed on a helper is handed to the primary connection.
    /// </summary>
    /// <param name="session">The IMAP session.</param>
    /// <param name="run">The state shared by the workers.</param>
    /// <param name="folder">The folder.</param>
    /// <param name="isHelper">Whether the session is an additional connection.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> if the folder was synchronized.</returns>
    private async Task<bool> TrySyncFolderAsync(
        IImapSession session,
        SyncRun run,
        MailFolder folder,
        bool isHelper,
        CancellationToken cancellationToken)
    {
        try
        {
            var (newMessages, changed) = await SyncFolderAsync(session, run, folder, cancellationToken);
            run.AddSynced();
            if (changed && folder.Role == FolderRole.Inbox)
            {
                run.OnInboxChanged?.Invoke(newMessages);
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (isHelper)
            {
                // The connection itself may be the problem, so the folder gets one more attempt on the primary one.
                logger.LogWarning(ex, "Folder {Folder} of account {Account} failed on an additional connection; retrying.", folder.FullName, run.Account.EmailAddress);
                run.Retry.Enqueue(folder);
            }
            else
            {
                // One broken folder must not prevent the remaining folders from synchronizing.
                logger.LogWarning(ex, "Failed to synchronize folder {Folder} of account {Account}.", folder.FullName, run.Account.EmailAddress);
                run.AddFailed(folder.FullName);
            }

            return false;
        }
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
    /// <param name="run">The state shared by the workers.</param>
    /// <param name="folder">The folder.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of new messages, and whether anything in the cache changed.</returns>
    private async Task<(int NewMessages, bool Changed)> SyncFolderAsync(IImapSession session, SyncRun run, MailFolder folder, CancellationToken cancellationToken)
    {
        var uidValidity = await session.GetUidValidityAsync(folder.FullName, cancellationToken);

        // The entity is changed inside the gate too: a save of another worker would otherwise pick up a half-finished change.
        // The flag lock keeps the immediate push away from cached UIDs while they are being discarded or pushed here.
        var invalidated = await _flags.RunExclusiveAsync(
            folder.Id,
            async () =>
            {
                var discarded = await run.Database.RunAsync(
                    async () =>
                    {
                        var changed = folder.UpdateUidValidity(uidValidity);
                        await folders.SaveChangesAsync(cancellationToken);
                        if (changed)
                        {
                            logger.LogInformation("UIDVALIDITY of {Folder} changed; discarding cached messages.", folder.FullName);
                            await messages.DeleteByFolderAsync(folder.AccountId, folder.Id, cancellationToken);
                        }

                        return changed;
                    },
                    cancellationToken);
                if (!discarded)
                {
                    // Local flag changes go up before anything is pulled, so a pull never reverts them.
                    await PushPendingFlagsAsync(session, run.Database, folder, cancellationToken);
                }

                return discarded;
            },
            cancellationToken);

        var afterUid = await run.Database.RunAsync(
            () => messages.GetMaxUidAsync(folder.AccountId, folder.Id, cancellationToken),
            cancellationToken);

        // An empty local folder only gets the newest few messages so the user sees mail at once; the rest is backfilled.
        var limit = afterUid == 0
            ? Math.Min(options.InitialFetchCount, options.MaxMessagesPerFolderPerSync)
            : options.MaxMessagesPerFolderPerSync;
        var remote = await session.FetchNewMessagesAsync(
            folder.FullName,
            afterUid,
            limit,
            cancellationToken);
        var created = remote.Select(r => ToMessage(folder, r)).ToList();
        if (created.Count > 0)
        {
            await run.Database.RunAsync(() => messages.AddRangeAsync(created, cancellationToken), cancellationToken);

            // Counted as soon as they are stored, so they are reported even if a later step of the folder fails.
            run.AddNewMessages(created.Count);
        }

        // Under the flag lock: flags read before an immediate push must not be applied after it.
        var pulled = !invalidated && await _flags.RunExclusiveAsync(
            folder.Id,
            () => PullFlagsAsync(session, run.Database, folder, cancellationToken),
            cancellationToken);
        return (created.Count, invalidated || created.Count > 0 || pulled);
    }

    /// <summary>
    /// Writes the read and starred state of locally changed messages to the server.
    /// </summary>
    /// <param name="session">The IMAP session.</param>
    /// <param name="database">Serializes the database work.</param>
    /// <param name="folder">The folder.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the pending changes are pushed.</returns>
    private async Task PushPendingFlagsAsync(IImapSession session, DatabaseGate database, MailFolder folder, CancellationToken cancellationToken)
    {
        var pending = await database.RunAsync(
            () => messages.GetPendingFlagStatesAsync(folder.AccountId, folder.Id, cancellationToken),
            cancellationToken);
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
        var cleared = await database.RunAsync(
            () => messages.ClearFlagSyncPendingAsync(folder.AccountId, pending, cancellationToken),
            cancellationToken);
        logger.LogInformation("Pushed flags of {Count} messages in {Folder}.", cleared, folder.FullName);
    }

    /// <summary>
    /// Copies server-side flags onto cached messages and optionally removes messages deleted on the server.
    /// </summary>
    /// <param name="session">The IMAP session.</param>
    /// <param name="database">Serializes the database work.</param>
    /// <param name="folder">The folder.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> if a cached message was changed or removed.</returns>
    private async Task<bool> PullFlagsAsync(IImapSession session, DatabaseGate database, MailFolder folder, CancellationToken cancellationToken)
    {
        var cached = await database.RunAsync(
            () => messages.GetFlagStatesAsync(folder.AccountId, folder.Id, cancellationToken),
            cancellationToken);
        if (cached.Count == 0)
        {
            return false;
        }

        var remote = await session.FetchFlagsAsync(folder.FullName, cached[0].Uid, cancellationToken);
        var remoteByUid = remote.ToDictionary(r => r.Uid);
        var gone = new List<Guid>();
        var changed = false;
        foreach (var message in cached)
        {
            if (remoteByUid.TryGetValue(message.Uid, out var state) && !state.IsDeleted)
            {
                // The repository skips pending rows in the statement itself, so a change the user made after the
                // snapshot was taken is not overwritten either.
                if (!message.IsFlagSyncPending && (message.IsRead != state.IsRead || message.IsFlagged != state.IsFlagged))
                {
                    await database.RunAsync(
                        () => messages.ApplyServerFlagsAsync(folder.AccountId, message.Id, state.IsRead, state.IsFlagged, cancellationToken),
                        cancellationToken);
                    changed = true;
                }
            }
            else
            {
                gone.Add(message.Id);
            }
        }

        if (gone.Count == 0 || !settings.RemoveServerDeletedMessages)
        {
            return changed;
        }

        if (remote.Count == 0)
        {
            // An empty answer for a non-empty cache is more likely a server hiccup than a wiped mailbox.
            logger.LogWarning(
                "Server returned no flags for {Folder} although {Count} messages are cached; removing nothing.",
                folder.FullName,
                cached.Count);
            return changed;
        }

        await database.RunAsync(() => messages.DeleteRangeAsync(folder.AccountId, gone, cancellationToken), cancellationToken);
        logger.LogInformation("Removed {Count} messages deleted on the server from {Folder}.", gone.Count, folder.FullName);
        return true;
    }

    /// <summary>
    /// Converts a downloaded message into a cache entity.
    /// </summary>
    /// <param name="folder">The containing folder.</param>
    /// <param name="remote">The downloaded message.</param>
    /// <returns>The entity.</returns>
    internal static MailMessage ToMessage(MailFolder folder, RemoteMessage remote)
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

    /// <summary>
    /// The state the folder workers of one sync share.
    /// </summary>
    /// <param name="account">The account being synchronized.</param>
    /// <param name="work">The folders to synchronize, most important first.</param>
    /// <param name="onInboxChanged">Receives the early INBOX report; optional.</param>
    private sealed class SyncRun(MailAccount account, IEnumerable<MailFolder> work, Action<int>? onInboxChanged) : IDisposable
    {
        /// <summary>
        /// Guards the result counters.
        /// </summary>
        private readonly Lock _sync = new();

        /// <summary>
        /// The names of the folders that failed.
        /// </summary>
        private readonly List<string> _failed = [];

        /// <summary>
        /// Gets the account being synchronized.
        /// </summary>
        public MailAccount Account { get; } = account;

        /// <summary>
        /// Gets the receiver of the early INBOX report, if any.
        /// </summary>
        public Action<int>? OnInboxChanged { get; } = onInboxChanged;

        /// <summary>
        /// Gets the folders no worker has taken yet.
        /// </summary>
        public ConcurrentQueue<MailFolder> Queue { get; } = new(work);

        /// <summary>
        /// Gets the folders that failed on an additional connection and wait for the primary one.
        /// </summary>
        public ConcurrentQueue<MailFolder> Retry { get; } = new();

        /// <summary>
        /// Gets the gate that serializes the database work.
        /// </summary>
        public DatabaseGate Database { get; } = new();

        /// <summary>
        /// Gets the number of folders synchronized; read after the workers have ended.
        /// </summary>
        public int Synced { get; private set; }

        /// <summary>
        /// Gets the number of new messages cached; read after the workers have ended.
        /// </summary>
        public int NewMessages { get; private set; }

        /// <summary>
        /// Gets the folders that failed; read after the workers have ended.
        /// </summary>
        public List<string> Failed => _failed;

        /// <summary>
        /// Records a synchronized folder.
        /// </summary>
        public void AddSynced()
        {
            lock (_sync)
            {
                Synced++;
            }
        }

        /// <summary>
        /// Records newly cached messages.
        /// </summary>
        /// <param name="count">The number of messages.</param>
        public void AddNewMessages(int count)
        {
            lock (_sync)
            {
                NewMessages += count;
            }
        }

        /// <summary>
        /// Records a failed folder.
        /// </summary>
        /// <param name="folderFullName">The folder.</param>
        public void AddFailed(string folderFullName)
        {
            lock (_sync)
            {
                _failed.Add(folderFullName);
            }
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Database.Dispose();
        }
    }
}
