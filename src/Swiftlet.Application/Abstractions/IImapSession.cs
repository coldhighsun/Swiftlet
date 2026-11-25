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
    /// Deletes a message on the server.
    /// A message that is already gone counts as deleted; every other failure is thrown.
    /// </summary>
    /// <param name="folderFullName">The full server-side path of the folder holding the message.</param>
    /// <param name="uid">The IMAP UID of the message.</param>
    /// <param name="trashFolderFullName">The Trash folder to move the message to, or <see langword="null"/> to delete it permanently.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the server no longer holds the message in the source folder.</returns>
    Task DeleteMessageAsync(
        string folderFullName,
        uint uid,
        string? trashFolderFullName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends an RFC 5322 message to the Sent folder, marked as read.
    /// </summary>
    /// <param name="rfc822">The raw message.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> if a Sent folder was found and the message appended.</returns>
    Task<bool> AppendToSentAsync(ReadOnlyMemory<byte> rfc822, CancellationToken cancellationToken = default);
}
