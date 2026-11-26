using Swiftlet.Domain.Folders;

namespace Swiftlet.Application.Abstractions;

/// <summary>
/// A folder as reported by an IMAP server.
/// </summary>
/// <param name="FullName">The full server-side path.</param>
/// <param name="Name">The display name.</param>
/// <param name="Role">The well-known role.</param>
/// <param name="CanSelect">Whether the folder can hold messages and be opened.</param>
public sealed record RemoteFolder(string FullName, string Name, FolderRole Role, bool CanSelect);

/// <summary>
/// Attachment metadata of a downloaded message.
/// </summary>
/// <param name="FileName">The file name.</param>
/// <param name="ContentType">The MIME content type.</param>
/// <param name="Size">The size in bytes.</param>
public sealed record RemoteAttachment(string FileName, string ContentType, long Size);

/// <summary>
/// The desired read and starred state of one message, to be written to the server.
/// </summary>
/// <param name="Uid">The IMAP UID.</param>
/// <param name="IsRead">Whether the server should hold the message as seen.</param>
/// <param name="IsFlagged">Whether the server should hold the message as flagged (starred).</param>
public sealed record FlagUpdate(uint Uid, bool IsRead, bool IsFlagged);

/// <summary>
/// The flags the server currently holds for one message.
/// </summary>
/// <param name="Uid">The IMAP UID.</param>
/// <param name="IsRead">Whether the message is flagged as seen.</param>
/// <param name="IsFlagged">Whether the message is flagged (starred).</param>
/// <param name="IsDeleted">Whether the message carries the deleted flag.</param>
public sealed record RemoteFlagState(uint Uid, bool IsRead, bool IsFlagged, bool IsDeleted);

/// <summary>
/// The envelope-level data of one message, available without downloading its body.
/// </summary>
/// <param name="Uid">The IMAP UID.</param>
/// <param name="MessageId">The Message-ID header value without angle brackets, if any.</param>
/// <param name="IsRead">Whether the message is flagged as seen on the server.</param>
/// <param name="InternalDate">The server's internal date, used when the message has no usable Date header.</param>
public sealed record RemoteHeader(uint Uid, string? MessageId, bool IsRead, DateTimeOffset InternalDate);

/// <summary>
/// One batch of older messages of a folder, listed without their bodies.
/// </summary>
/// <param name="Headers">The listed messages, ordered by UID ascending.</param>
/// <param name="Remaining">How many still older messages the server holds after this batch.</param>
/// <param name="OldestUid">The lowest UID this batch covered; 0 when the batch is empty.</param>
public sealed record OlderHeadersPage(IReadOnlyList<RemoteHeader> Headers, int Remaining, uint OldestUid);

/// <summary>
/// A message downloaded from an IMAP server.
/// </summary>
/// <param name="Uid">The IMAP UID.</param>
/// <param name="MessageId">The Message-ID header value, if any.</param>
/// <param name="Subject">The subject.</param>
/// <param name="FromName">The sender's display name.</param>
/// <param name="FromAddress">The sender's address.</param>
/// <param name="Recipients">The comma-separated recipient addresses.</param>
/// <param name="SentAt">The time the message was sent.</param>
/// <param name="BodyText">The plain-text body.</param>
/// <param name="BodyHtml">The HTML body, if any.</param>
/// <param name="IsRead">Whether the message is flagged as seen on the server.</param>
/// <param name="Attachments">The attachment metadata.</param>
public sealed record RemoteMessage(
    uint Uid,
    string? MessageId,
    string Subject,
    string FromName,
    string FromAddress,
    string Recipients,
    DateTimeOffset SentAt,
    string BodyText,
    string? BodyHtml,
    bool IsRead,
    IReadOnlyList<RemoteAttachment> Attachments);
