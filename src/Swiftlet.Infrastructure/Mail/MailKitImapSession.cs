using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;
using Swiftlet.Application.Abstractions;
using Swiftlet.Domain.Folders;

namespace Swiftlet.Infrastructure.Mail;

/// <summary>
/// An <see cref="IImapSession"/> backed by a MailKit <see cref="ImapClient"/>.
/// </summary>
/// <param name="client">The connected and authenticated client; owned by the session.</param>
internal sealed class MailKitImapSession(ImapClient client) : IImapSession
{
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
        var folder = await client.GetFolderAsync(folderFullName, cancellationToken);
        await folder.StatusAsync(StatusItems.UidValidity, cancellationToken);
        return folder.UidValidity;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RemoteMessage>> FetchNewMessagesAsync(
        string folderFullName,
        uint afterUid,
        int maxCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);

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
    public async Task<byte[]?> DownloadAttachmentAsync(
        string folderFullName,
        uint uid,
        int ordinal,
        CancellationToken cancellationToken = default)
    {
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

        var part = message.Attachments.OfType<MimePart>().ElementAtOrDefault(ordinal);
        if (part?.Content is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        await part.Content.DecodeToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    /// <inheritdoc />
    public async Task DeleteMessageAsync(
        string folderFullName,
        uint uid,
        string? trashFolderFullName,
        CancellationToken cancellationToken = default)
    {
        if (trashFolderFullName is null && !client.Capabilities.Contains(ImapCapability.UidPlus))
        {
            // Checked before any mutation: without UIDPLUS only a plain EXPUNGE exists, which would purge unrelated
            // flagged mail, and a \Deleted flag left behind would let any other client's EXPUNGE delete this message.
            throw new NotSupportedException(
                "The server does not support UIDPLUS, so a single message cannot be permanently deleted safely.");
        }

        var folder = await client.GetFolderAsync(folderFullName, cancellationToken);
        await folder.OpenAsync(FolderAccess.ReadWrite, cancellationToken);
        var uniqueId = new UniqueId(uid);

        try
        {
            if (trashFolderFullName is null)
            {
                // Permanent delete: flag and expunge only this UID (UID EXPUNGE, guarded by UIDPLUS above).
                // The server-side behaviour (expunge failing after the flag, flag undo) cannot be unit-tested
                // without a real IMAP server.
                await folder.AddFlagsAsync(uniqueId, MessageFlags.Deleted, true, cancellationToken);
                try
                {
                    await folder.ExpungeAsync([uniqueId], cancellationToken);
                }
                catch (Exception)
                {
                    // Undo the flag so another client's later EXPUNGE cannot silently purge the message, then
                    // rethrow the original error (the outer handler still treats an already-gone UID as success).
                    await TryUndoDeletedFlagAsync(folder, uniqueId);
                    throw;
                }
            }
            else
            {
                // MoveToAsync uses MOVE when available and falls back to COPY + delete otherwise.
                var trash = await client.GetFolderAsync(trashFolderFullName, cancellationToken);
                await folder.MoveToAsync(uniqueId, trash, cancellationToken);
            }
        }
        catch (ImapCommandException)
        {
            // A server may answer NO/BAD when the UID was already removed (for example by another client).
            // Deleting a message that is gone is the desired end state, but only if the UID is truly absent;
            // any other command failure is rethrown.
            var remaining = await folder.SearchAsync(SearchQuery.Uids([uniqueId]), cancellationToken);
            if (remaining.Count > 0)
            {
                throw;
            }
        }
    }

    /// <summary>
    /// Best-effort removal of the <c>\Deleted</c> flag after a failed expunge so another client cannot purge the message later.
    /// </summary>
    /// <param name="folder">The open folder.</param>
    /// <param name="uniqueId">The UID of the message.</param>
    /// <returns>A task that completes when the attempt has finished.</returns>
    private static async Task TryUndoDeletedFlagAsync(IMailFolder folder, UniqueId uniqueId)
    {
        try
        {
            await folder.RemoveFlagsAsync(uniqueId, MessageFlags.Deleted, true);
        }
        catch (Exception ex) when (ex is IOException or ServiceNotConnectedException or ProtocolException or ImapCommandException)
        {
            // Best effort: the connection or the message may be gone; the original expunge error is what matters.
        }
    }

    /// <inheritdoc />
    public async Task<bool> AppendToSentAsync(ReadOnlyMemory<byte> rfc822, CancellationToken cancellationToken = default)
    {
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

        return (attributes & FolderAttributes.Archive) != 0 ? FolderRole.Archive : FolderRole.Other;
    }
}
