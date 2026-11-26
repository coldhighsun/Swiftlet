using System.Runtime.CompilerServices;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using Microsoft.Extensions.Logging;
using MimeKit;
using Swiftlet.Application.Abstractions;
using Swiftlet.Domain.Folders;

namespace Swiftlet.Infrastructure.Mail;

/// <summary>
/// An <see cref="IImapSession"/> backed by a MailKit <see cref="ImapClient"/>.
/// </summary>
/// <param name="client">The connected and authenticated client; owned by the session.</param>
/// <param name="logger">The logger.</param>
internal sealed class MailKitImapSession(ImapClient client, ILogger<MailKitImapSession> logger) : IImapSession
{
    /// <summary>
    /// The last observed state of each waited-on folder.
    /// </summary>
    private readonly FolderChangeTracker _changes = new();

    /// <inheritdoc />
    public async Task<IReadOnlyList<RemoteFolder>> ListFoldersAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<RemoteFolder>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        Add(client.Inbox, result, seen);
        if (client.PersonalNamespaces.Count > 0)
        {
            var root = client.GetFolder(client.PersonalNamespaces[0]);
            await CollectAsync(root, result, seen, cancellationToken);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<uint> GetUidValidityAsync(string folderFullName, CancellationToken cancellationToken = default)
    {
        try
        {
            var folder = await client.GetFolderAsync(folderFullName, cancellationToken);
            await folder.StatusAsync(StatusItems.UidValidity, cancellationToken);
            return folder.UidValidity;
        }
        catch (Exception ex) when (ex is ImapCommandException or FolderNotFoundException)
        {
            throw Rejected(folderFullName, ex);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RemoteMessage>> FetchNewMessagesAsync(
        string folderFullName,
        uint afterUid,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        logger.LogDebug("Fetching up to {Max} messages after UID {Uid} in {Folder}", maxCount, afterUid, folderFullName);

        var folder = await client.GetFolderAsync(folderFullName, cancellationToken);
        await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

        var query = afterUid == 0
            ? SearchQuery.All
            : SearchQuery.Uids(new UniqueIdRange(new UniqueId(afterUid + 1), UniqueId.MaxValue));

        // Skip messages still flagged \Deleted: on servers with neither MOVE nor UIDPLUS, MailKit's COPY fallback
        // leaves the source flagged rather than expunged, and those must not be downloaded again.
        // Not unit-testable without a real IMAP server.
        query = query.And(SearchQuery.NotDeleted);
        var found = await folder.SearchAsync(query, cancellationToken);

        // "UID n:*" always matches the last message even when it is older than n, so filter explicitly.
        var ordered = found.Where(u => u.Id > afterUid).OrderBy(u => u.Id).ToList();
        var selected = afterUid == 0
            ? ordered.TakeLast(maxCount).ToList()
            : ordered.Take(maxCount).ToList();
        if (selected.Count == 0)
        {
            return [];
        }

        var summaries = await folder.FetchAsync(
            selected,
            MessageSummaryItems.UniqueId | MessageSummaryItems.Flags | MessageSummaryItems.InternalDate,
            cancellationToken);
        var byUid = summaries.ToDictionary(s => s.UniqueId.Id);

        var result = new List<RemoteMessage>(selected.Count);
        foreach (var uid in selected)
        {
            var mime = await folder.GetMessageAsync(uid, cancellationToken);
            byUid.TryGetValue(uid.Id, out var summary);
            var isRead = summary?.Flags is { } flags && flags.HasFlag(MessageFlags.Seen);
            var fallback = summary?.InternalDate ?? DateTimeOffset.UtcNow;
            result.Add(MimeMessageMapper.Map(uid.Id, mime, isRead, fallback));
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<OlderHeadersPage> ListOlderAsync(
        string folderFullName,
        uint beforeUid,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        logger.LogDebug("Listing up to {Max} messages before UID {Uid} in {Folder}", maxCount, beforeUid, folderFullName);

        var folder = await client.GetFolderAsync(folderFullName, cancellationToken);
        await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

        // The server's own message count next to what SEARCH returns tells a truncated SEARCH from a genuinely small mailbox.
        logger.LogDebug(
            "Folder {Folder} on the server: {Exists} messages, UIDNEXT {UidNext}, UIDVALIDITY {UidValidity}; backfilling below UID {Uid}",
            folderFullName,
            folder.Count,
            folder.UidNext?.Id,
            folder.UidValidity,
            beforeUid);

        if (beforeUid <= 1)
        {
            return new OlderHeadersPage([], 0, 0);
        }

        // Same \Deleted exclusion as FetchNewMessagesAsync.
        var query = SearchQuery.Uids(new UniqueIdRange(UniqueId.MinValue, new UniqueId(beforeUid - 1))).And(SearchQuery.NotDeleted);
        var found = await folder.SearchAsync(query, cancellationToken);
        var ordered = found.Where(u => u.Id < beforeUid).OrderBy(u => u.Id).ToList();
        var selected = ordered.TakeLast(maxCount).ToList();
        var remaining = ordered.Count - selected.Count;
        logger.LogDebug(
            "Server reports {Found} messages older than UID {Uid} in {Folder}; listing {Selected}",
            ordered.Count,
            beforeUid,
            folderFullName,
            selected.Count);
        if (selected.Count == 0)
        {
            return new OlderHeadersPage([], 0, 0);
        }

        // The envelope carries the Message-ID, so the caller can recognise messages it already holds without downloading them.
        var summaries = await folder.FetchAsync(
            selected,
            MessageSummaryItems.UniqueId | MessageSummaryItems.Flags | MessageSummaryItems.InternalDate | MessageSummaryItems.Envelope,
            cancellationToken);
        var byUid = summaries.ToDictionary(s => s.UniqueId.Id);

        var headers = selected
            .Select(uid =>
            {
                byUid.TryGetValue(uid.Id, out var summary);
                var isRead = summary?.Flags is { } flags && flags.HasFlag(MessageFlags.Seen);
                return new RemoteHeader(
                    uid.Id,
                    string.IsNullOrWhiteSpace(summary?.Envelope?.MessageId) ? null : summary.Envelope.MessageId,
                    isRead,
                    summary?.InternalDate ?? DateTimeOffset.UtcNow);
            })
            .ToList();
        return new OlderHeadersPage(headers, remaining, selected[0].Id);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RemoteMessage>> FetchMessagesAsync(
        string folderFullName,
        IReadOnlyList<RemoteHeader> headers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(headers);
        if (headers.Count == 0)
        {
            return [];
        }

        logger.LogDebug("Downloading {Count} listed messages in {Folder}", headers.Count, folderFullName);
        var folder = await client.GetFolderAsync(folderFullName, cancellationToken);

        // MailKit sends SELECT again even for the folder that is already selected; the backfill asks for one message at a
        // time, so that would cost a round trip per message. Not unit-testable without a real IMAP server.
        if (!folder.IsOpen)
        {
            await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
        }

        var result = new List<RemoteMessage>(headers.Count);
        foreach (var header in headers)
        {
            try
            {
                var mime = await folder.GetMessageAsync(new UniqueId(header.Uid), cancellationToken);
                result.Add(MimeMessageMapper.Map(header.Uid, mime, header.IsRead, header.InternalDate));
            }
            catch (MessageNotFoundException)
            {
                // The message was expunged on the server between the listing and the download; the history simply lacks it.
                logger.LogDebug("UID {Uid} in {Folder} disappeared during backfill", header.Uid, folderFullName);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<byte[]?> DownloadAttachmentAsync(
        string folderFullName,
        uint uid,
        int ordinal,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Downloading attachment {Ordinal} of UID {Uid} in {Folder}", ordinal, uid, folderFullName);
        var folder = await client.GetFolderAsync(folderFullName, cancellationToken);
        await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

        MimeMessage message;
        try
        {
            message = await folder.GetMessageAsync(new UniqueId(uid), cancellationToken);
        }
        catch (MessageNotFoundException)
        {
            // The message was deleted on the server since it was synchronized.
            return null;
        }

        var part = MimeAttachments.Enumerate(message).ElementAtOrDefault(ordinal);
        if (part?.Content is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        await part.Content.DecodeToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, InlineImage>?> DownloadInlineImagesAsync(
        string folderFullName,
        uint uid,
        IReadOnlyCollection<string> contentIds,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Downloading inline images of UID {Uid} in {Folder}: {Count} wanted", uid, folderFullName, contentIds.Count);
        var folder = await client.GetFolderAsync(folderFullName, cancellationToken);
        await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

        MimeMessage message;
        try
        {
            message = await folder.GetMessageAsync(new UniqueId(uid), cancellationToken);
        }
        catch (MessageNotFoundException)
        {
            // The message was deleted on the server since it was synchronized.
            return null;
        }

        return InlineImageExtractor.Extract(message, contentIds, logger);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<uint, uint>> DeleteMessagesAsync(
        string folderFullName,
        IReadOnlyList<uint> uids,
        string? trashFolderFullName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uids);
        var result = new Dictionary<uint, uint>();
        if (uids.Count == 0)
        {
            return result;
        }

        logger.LogInformation(
            "Deleting {Count} messages in {Folder} ({Mode})",
            uids.Count,
            folderFullName,
            trashFolderFullName is null ? "permanent" : "move to trash");
        if (trashFolderFullName is null && !client.Capabilities.Contains(ImapCapability.UidPlus))
        {
            // Checked before any mutation: without UIDPLUS only a plain EXPUNGE exists, which would purge unrelated
            // flagged mail, and a \Deleted flag left behind would let any other client's EXPUNGE delete these messages.
            throw new NotSupportedException(
                "The server does not support UIDPLUS, so single messages cannot be permanently deleted safely.");
        }

        var uniqueIds = uids.Select(u => new UniqueId(u)).ToList();
        try
        {
            var folder = await client.GetFolderAsync(folderFullName, cancellationToken);
            await folder.OpenAsync(FolderAccess.ReadWrite, cancellationToken);
            try
            {
                if (trashFolderFullName is null)
                {
                    // Permanent delete: flag and expunge only these UIDs (UID EXPUNGE, guarded by UIDPLUS above).
                    // The server-side behaviour (expunge failing after the flag, flag undo) cannot be unit-tested
                    // without a real IMAP server.
                    await folder.AddFlagsAsync(uniqueIds, MessageFlags.Deleted, true, cancellationToken);
                    try
                    {
                        await folder.ExpungeAsync(uniqueIds, cancellationToken);
                    }
                    catch (Exception)
                    {
                        // Undo the flag so another client's later EXPUNGE cannot silently purge the messages, then
                        // rethrow the original error (the outer handler still treats already-gone UIDs as success).
                        await TryUndoDeletedFlagAsync(folder, uniqueIds);
                        throw;
                    }
                }
                else
                {
                    // MoveToAsync uses MOVE when available and falls back to COPY + delete otherwise.
                    var trash = await client.GetFolderAsync(trashFolderFullName, cancellationToken);
                    var moved = await folder.MoveToAsync(uniqueIds, trash, cancellationToken);
                    foreach (var (source, destination) in moved.Source.Zip(moved.Destination))
                    {
                        if (destination.IsValid)
                        {
                            result[source.Id] = destination.Id;
                        }
                    }
                }
            }
            catch (ImapCommandException)
            {
                // A server may answer NO/BAD when the UIDs were already removed (for example by another client).
                // Deleting messages that are gone is the desired end state, but only if they are truly absent;
                // any other command failure is rethrown.
                var remaining = await folder.SearchAsync(SearchQuery.Uids(uniqueIds), cancellationToken);
                if (remaining.Count > 0)
                {
                    throw;
                }
            }
        }
        catch (Exception ex) when (ex is ImapCommandException or FolderNotFoundException)
        {
            throw Rejected(folderFullName, ex);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<uint>> ListUidsBetweenAsync(
        string folderFullName,
        uint afterUid,
        uint beforeUid,
        CancellationToken cancellationToken = default)
    {
        if (beforeUid <= (long)afterUid + 1)
        {
            return [];
        }

        try
        {
            var folder = await client.GetFolderAsync(folderFullName, cancellationToken);
            await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

            // The same \Deleted exclusion as the regular sync, so messages it would never download do not count.
            var query = SearchQuery.Uids(new UniqueIdRange(new UniqueId(afterUid + 1), new UniqueId(beforeUid - 1))).And(SearchQuery.NotDeleted);
            var found = await folder.SearchAsync(query, cancellationToken);
            return [.. found.Select(u => u.Id).Where(u => u > afterUid && u < beforeUid).Order()];
        }
        catch (Exception ex) when (ex is ImapCommandException or FolderNotFoundException)
        {
            throw Rejected(folderFullName, ex);
        }
    }

    /// <summary>
    /// Translates a refusal of the server into the exception the application layer understands.
    /// </summary>
    /// <param name="folderFullName">The folder the command was about.</param>
    /// <param name="error">The MailKit error.</param>
    /// <returns>The exception to throw.</returns>
    private static MailOperationRejectedException Rejected(string folderFullName, Exception error)
    {
        return new MailOperationRejectedException($"The server refused the operation on '{folderFullName}': {error.Message}", error);
    }

    /// <summary>
    /// Best-effort removal of the <c>\Deleted</c> flag after a failed expunge so another client cannot purge the message later.
    /// </summary>
    /// <param name="folder">The open folder.</param>
    /// <param name="uniqueIds">The UIDs of the messages.</param>
    /// <returns>A task that completes when the attempt has finished.</returns>
    private static async Task TryUndoDeletedFlagAsync(IMailFolder folder, IList<UniqueId> uniqueIds)
    {
        try
        {
            await folder.RemoveFlagsAsync(uniqueIds, MessageFlags.Deleted, true);
        }
        catch (Exception ex) when (ex is IOException or ServiceNotConnectedException or ProtocolException or ImapCommandException)
        {
            // Best effort: the connection or the message may be gone; the original expunge error is what matters.
        }
    }

    /// <inheritdoc />
    public async Task SetFlagsAsync(
        string folderFullName,
        IReadOnlyList<FlagUpdate> updates,
        CancellationToken cancellationToken = default)
    {
        if (updates.Count == 0)
        {
            return;
        }

        logger.LogDebug("Setting flags of {Count} messages in {Folder}", updates.Count, folderFullName);
        var folder = await client.GetFolderAsync(folderFullName, cancellationToken);
        await folder.OpenAsync(FolderAccess.ReadWrite, cancellationToken);

        // Group by target value so at most four STORE commands are issued. Only \Seen and \Flagged are touched, so
        // other flags such as \Answered survive. A UID that no longer exists is simply ignored by servers on STORE,
        // and errors are deliberately not swallowed. Not unit-testable without a real IMAP server.
        await StoreAsync(folder, updates.Where(u => u.IsRead), MessageFlags.Seen, true, cancellationToken);
        await StoreAsync(folder, updates.Where(u => !u.IsRead), MessageFlags.Seen, false, cancellationToken);
        await StoreAsync(folder, updates.Where(u => u.IsFlagged), MessageFlags.Flagged, true, cancellationToken);
        await StoreAsync(folder, updates.Where(u => !u.IsFlagged), MessageFlags.Flagged, false, cancellationToken);
    }

    /// <summary>
    /// Adds or removes one flag on a set of messages with a single silent STORE command.
    /// </summary>
    /// <param name="folder">The open read-write folder.</param>
    /// <param name="updates">The updates whose messages should be changed.</param>
    /// <param name="flag">The flag to add or remove.</param>
    /// <param name="add"><see langword="true"/> to add the flag, <see langword="false"/> to remove it.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the command has finished.</returns>
    private static async Task StoreAsync(
        IMailFolder folder,
        IEnumerable<FlagUpdate> updates,
        MessageFlags flag,
        bool add,
        CancellationToken cancellationToken)
    {
        var uids = updates.Select(u => new UniqueId(u.Uid)).ToList();
        if (uids.Count == 0)
        {
            return;
        }

        if (add)
        {
            await folder.AddFlagsAsync(uids, flag, true, cancellationToken);
        }
        else
        {
            await folder.RemoveFlagsAsync(uids, flag, true, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RemoteFlagState>> FetchFlagsAsync(
        string folderFullName,
        uint minUid,
        CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Fetching flags from UID {Uid} in {Folder}", minUid, folderFullName);
        var folder = await client.GetFolderAsync(folderFullName, cancellationToken);
        await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

        var summaries = await folder.FetchAsync(
            new UniqueIdRange(new UniqueId(Math.Max(1u, minUid)), UniqueId.MaxValue),
            MessageSummaryItems.UniqueId | MessageSummaryItems.Flags,
            cancellationToken);

        // "UID n:*" always matches the last message even when it is older than n, so filter explicitly.
        // Not unit-testable without a real IMAP server.
        return summaries
            .Where(s => s.UniqueId.Id >= minUid)
            .OrderBy(s => s.UniqueId.Id)
            .Select(s =>
            {
                var flags = s.Flags ?? MessageFlags.None;
                return new RemoteFlagState(
                    s.UniqueId.Id,
                    flags.HasFlag(MessageFlags.Seen),
                    flags.HasFlag(MessageFlags.Flagged),
                    flags.HasFlag(MessageFlags.Deleted));
            })
            .ToList();
    }

    /// <inheritdoc />
    public bool SupportsIdle => client.Capabilities.Contains(ImapCapability.Idle);

    /// <inheritdoc />
    public async Task<bool> WaitForInboxChangeAsync(
        string folderFullName,
        TimeSpan maxWait,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxWait, TimeSpan.Zero);

        var folder = await client.GetFolderAsync(folderFullName, cancellationToken);
        await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

        // The first observation on this session counts as a change, and changes delivered outside IDLE (between two waits)
        // never reach the handlers below, which are attached only after the SELECT so that it does not raise a spurious
        // CountChanged. Compare against the last observed state.
        if (_changes.Observe(folderFullName, folder.Count, folder.UidNext?.Id ?? 0))
        {
            return true;
        }

        // The handlers run on MailKit's reading thread, so the flag is written with Interlocked and read with Volatile.
        // Any event ends the IDLE by cancelling the done token, which makes MailKit send DONE; so does the timeout.
        // Not unit-testable without a real IMAP server.
        var changed = new StrongBox<int>();
        using var done = new CancellationTokenSource();

        // A user cancellation first ends the IDLE cleanly (DONE); the token passed to IdleAsync then aborts it.
        using var registration = cancellationToken.Register(done.Cancel);

        void OnChanged()
        {
            Interlocked.Exchange(ref changed.Value, 1);
            try
            {
                done.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The wait already finished and the source was disposed; the late event is irrelevant.
            }
        }

        void OnCountChanged(object? sender, EventArgs e) => OnChanged();
        void OnExpunged(object? sender, MessageEventArgs e) => OnChanged();
        void OnFlagsChanged(object? sender, MessageFlagsChangedEventArgs e) => OnChanged();

        folder.CountChanged += OnCountChanged;
        folder.MessageExpunged += OnExpunged;
        folder.MessageFlagsChanged += OnFlagsChanged;
        try
        {
            done.CancelAfter(maxWait);
            await client.IdleAsync(done.Token, cancellationToken);
        }
        finally
        {
            folder.CountChanged -= OnCountChanged;
            folder.MessageExpunged -= OnExpunged;
            folder.MessageFlagsChanged -= OnFlagsChanged;
        }

        // Record the count reached during the IDLE so the next wait compares against the latest folder state. MailKit updates
        // Count on an unsolicited EXISTS but UidNext only on SELECT/STATUS, so UidNext is recorded as unknown (0): the next
        // SELECT is then compared by count only, instead of reporting the already handled message a second time.
        _changes.Observe(folderFullName, folder.Count, 0);

        // A done-token cancellation is the normal way out, but a user cancellation must not look like a timeout.
        cancellationToken.ThrowIfCancellationRequested();
        return Volatile.Read(ref changed.Value) == 1;
    }

    /// <inheritdoc />
    public async Task<bool> AppendToSentAsync(ReadOnlyMemory<byte> rfc822, CancellationToken cancellationToken = default)
    {
        logger.LogDebug("Appending a {Size} byte copy to the Sent folder", rfc822.Length);
        IMailFolder? sent;
        try
        {
            sent = client.GetFolder(SpecialFolder.Sent);
        }
        catch (NotSupportedException)
        {
            // The server advertises neither SPECIAL-USE nor XLIST, so the Sent folder cannot be identified.
            return false;
        }

        if (sent is null)
        {
            return false;
        }

        using var stream = new MemoryStream(rfc822.ToArray());
        var message = await MimeMessage.LoadAsync(stream, cancellationToken);
        await sent.AppendAsync(message, MessageFlags.Seen, cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (client.IsConnected)
        {
            // Best effort: the connection may already be gone, and disposal must not mask the original error.
            try
            {
                await client.DisconnectAsync(true);
            }
            catch (Exception ex) when (ex is IOException or ServiceNotConnectedException or ProtocolException)
            {
                // Ignored: the connection is being discarded anyway.
            }
        }

        client.Dispose();
    }

    /// <summary>
    /// Recursively collects the subfolders of a folder.
    /// </summary>
    /// <param name="parent">The parent folder.</param>
    /// <param name="result">The result list.</param>
    /// <param name="seen">The full names already collected.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the subtree is collected.</returns>
    private static async Task CollectAsync(
        IMailFolder parent,
        List<RemoteFolder> result,
        HashSet<string> seen,
        CancellationToken cancellationToken)
    {
        var children = await parent.GetSubfoldersAsync(false, cancellationToken);
        foreach (var child in children)
        {
            Add(child, result, seen);
            await CollectAsync(child, result, seen, cancellationToken);
        }
    }

    /// <summary>
    /// Adds a folder to the result unless it was already collected.
    /// </summary>
    /// <param name="folder">The folder.</param>
    /// <param name="result">The result list.</param>
    /// <param name="seen">The full names already collected.</param>
    private static void Add(IMailFolder folder, List<RemoteFolder> result, HashSet<string> seen)
    {
        if (!seen.Add(folder.FullName))
        {
            return;
        }

        var attributes = folder.Attributes;
        var canSelect = (attributes & (FolderAttributes.NoSelect | FolderAttributes.NonExistent)) == 0;
        result.Add(new RemoteFolder(folder.FullName, folder.Name, ToRole(folder), canSelect));
    }

    /// <summary>
    /// Determines the well-known role of a folder from its IMAP attributes.
    /// </summary>
    /// <param name="folder">The folder.</param>
    /// <returns>The role.</returns>
    private static FolderRole ToRole(IMailFolder folder)
    {
        var attributes = folder.Attributes;
        if ((attributes & FolderAttributes.Inbox) != 0
            || string.Equals(folder.FullName, "INBOX", StringComparison.OrdinalIgnoreCase))
        {
            return FolderRole.Inbox;
        }

        if ((attributes & FolderAttributes.Sent) != 0)
        {
            return FolderRole.Sent;
        }

        if ((attributes & FolderAttributes.Drafts) != 0)
        {
            return FolderRole.Drafts;
        }

        if ((attributes & FolderAttributes.Trash) != 0)
        {
            return FolderRole.Trash;
        }

        if ((attributes & FolderAttributes.Junk) != 0)
        {
            return FolderRole.Junk;
        }

        if ((attributes & FolderAttributes.All) != 0)
        {
            return FolderRole.AllMail;
        }

        return (attributes & FolderAttributes.Archive) != 0 ? FolderRole.Archive : FolderRole.Other;
    }
}
