namespace Swiftlet.Application.Abstractions;

/// <summary>
/// An authenticated IMAP connection.
/// </summary>
public interface IImapSession : IAsyncDisposable
{
    /// <summary>
    /// Lists all folders of the mailbox, including nested ones.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The folders.</returns>
    Task<IReadOnlyList<RemoteFolder>> ListFoldersAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the UIDVALIDITY of a folder.
    /// </summary>
    /// <param name="folderFullName">The full server-side path of the folder.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The UIDVALIDITY value.</returns>
    Task<uint> GetUidValidityAsync(string folderFullName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads messages newer than a UID.
    /// When <paramref name="afterUid"/> is 0 the newest <paramref name="maxCount"/> messages are returned;
    /// otherwise the oldest <paramref name="maxCount"/> messages with a greater UID are returned, ascending,
    /// so repeated calls page forward without gaps.
    /// </summary>
    /// <param name="folderFullName">The full server-side path of the folder.</param>
    /// <param name="afterUid">Only messages with a UID greater than this are returned.</param>
    /// <param name="maxCount">The maximum number of messages to download.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The downloaded messages.</returns>
    Task<IReadOnlyList<RemoteMessage>> FetchNewMessagesAsync(
        string folderFullName,
        uint afterUid,
        int maxCount,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists the newest messages that are older than a UID without downloading their bodies, used to backfill history.
    /// </summary>
    /// <param name="folderFullName">The full server-side path of the folder.</param>
    /// <param name="beforeUid">Only messages with a UID smaller than this are listed.</param>
    /// <param name="maxCount">The maximum number of messages to list.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The listed messages (ascending by UID) and how many older ones remain on the server.</returns>
    Task<OlderHeadersPage> ListOlderAsync(
        string folderFullName,
        uint beforeUid,
        int maxCount,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads the full content of previously listed messages.
    /// A message that is gone from the server by now is left out of the result.
    /// </summary>
    /// <param name="folderFullName">The full server-side path of the folder.</param>
    /// <param name="headers">The listed messages to download.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The downloaded messages.</returns>
    Task<IReadOnlyList<RemoteMessage>> FetchMessagesAsync(
        string folderFullName,
        IReadOnlyList<RemoteHeader> headers,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads and decodes one attachment of a message.
    /// </summary>
    /// <param name="folderFullName">The full server-side path of the folder.</param>
    /// <param name="uid">The IMAP UID of the message.</param>
    /// <param name="ordinal">The zero-based attachment position, as reported when the message was synchronized.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The decoded content, or <see langword="null"/> if the message or attachment no longer exists.</returns>
    Task<byte[]?> DownloadAttachmentAsync(
        string folderFullName,
        uint uid,
        int ordinal,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads the images embedded in a message (parts with a Content-ID that HTML refers to as <c>cid:</c>).
    /// </summary>
    /// <param name="folderFullName">The full server-side path of the folder.</param>
    /// <param name="uid">The IMAP UID of the message.</param>
    /// <param name="contentIds">The normalized content identifiers wanted (see <c>InlineImageReferences.Normalize</c>).</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The images found, keyed by normalized content identifier; <see langword="null"/> if the message no longer exists.</returns>
    Task<IReadOnlyDictionary<string, InlineImage>?> DownloadInlineImagesAsync(
        string folderFullName,
        uint uid,
        IReadOnlyCollection<string> contentIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a message on the server.
    /// A message that is already gone counts as deleted; every other failure is thrown.
    /// </summary>
    /// <param name="folderFullName">The full server-side path of the folder holding the message.</param>
    /// <param name="uid">The IMAP UID of the message.</param>
    /// <param name="trashFolderFullName">The Trash folder to move the message to, or <see langword="null"/> to delete it permanently.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>
    /// The UID the message has in the Trash folder after a move, or <see langword="null"/> when it was deleted permanently,
    /// was already gone, or the server did not report the new UID (no UIDPLUS).
    /// </returns>
    Task<uint?> DeleteMessageAsync(
        string folderFullName,
        uint uid,
        string? trashFolderFullName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Tells whether a folder holds any message (not flagged as deleted) whose UID lies strictly between two UIDs.
    /// </summary>
    /// <param name="folderFullName">The full server-side path of the folder.</param>
    /// <param name="afterUid">The exclusive lower bound.</param>
    /// <param name="beforeUid">The exclusive upper bound.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> if at least one such message exists.</returns>
    Task<bool> HasMessagesBetweenAsync(
        string folderFullName,
        uint afterUid,
        uint beforeUid,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the read and starred state of messages to the server.
    /// Only the seen and flagged flags are changed; UIDs that no longer exist on the server are ignored.
    /// </summary>
    /// <param name="folderFullName">The full server-side path of the folder holding the messages.</param>
    /// <param name="updates">The target state per message.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the server holds the requested state.</returns>
    Task SetFlagsAsync(
        string folderFullName,
        IReadOnlyList<FlagUpdate> updates,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the flags of every message with a UID at or above a lower bound.
    /// </summary>
    /// <param name="folderFullName">The full server-side path of the folder.</param>
    /// <param name="minUid">Only messages with a UID greater than or equal to this are returned.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The flag states, ordered by UID.</returns>
    Task<IReadOnlyList<RemoteFlagState>> FetchFlagsAsync(
        string folderFullName,
        uint minUid,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a value indicating whether the server supports the IDLE extension, so <see cref="WaitForInboxChangeAsync"/> can be used.
    /// </summary>
    bool SupportsIdle { get; }

    /// <summary>
    /// Waits until the server reports a change in a folder, or until the maximum wait has elapsed.
    /// Also returns <see langword="true"/> at once on the first wait of a session (which records the baseline) and whenever the folder
    /// changed since the previous wait on this session, so changes that arrived between two waits are not missed.
    /// The connection is occupied for the whole wait and no other command may be issued on it meanwhile.
    /// </summary>
    /// <param name="folderFullName">The full server-side path of the folder to watch.</param>
    /// <param name="maxWait">The longest time to wait before returning without a change.</param>
    /// <param name="cancellationToken">The cancellation token; cancelling it throws <see cref="OperationCanceledException"/>.</param>
    /// <returns><see langword="true"/> if new, removed or changed messages were reported; <see langword="false"/> if <paramref name="maxWait"/> elapsed first.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxWait"/> is zero or negative.</exception>
    Task<bool> WaitForInboxChangeAsync(
        string folderFullName,
        TimeSpan maxWait,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends an RFC 5322 message to the Sent folder, marked as read.
    /// </summary>
    /// <param name="rfc822">The raw message.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> if a Sent folder was found and the message appended.</returns>
    Task<bool> AppendToSentAsync(ReadOnlyMemory<byte> rfc822, CancellationToken cancellationToken = default);
}
