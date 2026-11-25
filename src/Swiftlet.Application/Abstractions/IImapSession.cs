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
    /// Appends an RFC 5322 message to the Sent folder, marked as read.
    /// </summary>
    /// <param name="rfc822">The raw message.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> if a Sent folder was found and the message appended.</returns>
    Task<bool> AppendToSentAsync(ReadOnlyMemory<byte> rfc822, CancellationToken cancellationToken = default);
}
