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
