using System.Text;

namespace Swiftlet.Domain.Messages;

/// <summary>
/// A cached mail message.
/// </summary>
public sealed class MailMessage
{
    /// <summary>
    /// The maximum length of <see cref="Preview"/>.
    /// </summary>
    private const int PreviewLength = 200;

    /// <summary>
    /// Backing field of <see cref="Attachments"/>.
    /// </summary>
    private readonly List<MailAttachment> _attachments = [];

    /// <summary>
    /// Gets the unique identifier of the message.
    /// </summary>
    public Guid Id { get; private set; } = Guid.NewGuid();

    /// <summary>
    /// Gets the identifier of the owning account.
    /// </summary>
    public Guid AccountId { get; private set; }

    /// <summary>
    /// Gets the identifier of the folder containing the message.
    /// </summary>
    public Guid FolderId { get; private set; }

    /// <summary>
    /// Gets the IMAP UID of the message within its folder.
    /// </summary>
    public uint Uid { get; private set; }

    /// <summary>
    /// Gets the RFC 5322 Message-ID header value, if present.
    /// </summary>
    public string? MessageIdHeader { get; private set; }

    /// <summary>
    /// Gets the subject.
    /// </summary>
    public string Subject { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the sender's display name.
    /// </summary>
    public string FromName { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the sender's address.
    /// </summary>
    public string FromAddress { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the recipients as a comma-separated list of addresses.
    /// </summary>
    public string Recipients { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the time the message was sent.
    /// </summary>
    public DateTimeOffset SentAt { get; private set; }

    /// <summary>
    /// Gets the plain-text body.
    /// </summary>
    public string BodyText { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the HTML body, if any.
    /// </summary>
    public string? BodyHtml { get; private set; }

    /// <summary>
    /// Gets a short single-line preview of the body.
    /// </summary>
    public string Preview { get; private set; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether the message has been read.
    /// </summary>
    public bool IsRead { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the message is flagged (starred).
    /// </summary>
    public bool IsFlagged { get; private set; }

    /// <summary>
    /// Gets the attachments.
    /// </summary>
    public IReadOnlyList<MailAttachment> Attachments => _attachments;

    /// <summary>
    /// Initializes a new instance of the <see cref="MailMessage"/> class for persistence.
    /// </summary>
    private MailMessage()
    {
    }

    /// <summary>
    /// Creates a new cached message.
    /// </summary>
    /// <param name="accountId">The owning account.</param>
    /// <param name="folderId">The containing folder.</param>
    /// <param name="uid">The IMAP UID.</param>
    /// <param name="subject">The subject.</param>
    /// <param name="fromName">The sender's display name.</param>
    /// <param name="fromAddress">The sender's address.</param>
    /// <param name="recipients">The comma-separated recipients.</param>
    /// <param name="sentAt">The time the message was sent.</param>
    /// <param name="bodyText">The plain-text body.</param>
    /// <param name="bodyHtml">The HTML body, if any.</param>
    /// <param name="messageIdHeader">The Message-ID header value, if any.</param>
    /// <param name="isRead">Whether the message was already read on the server.</param>
    /// <returns>The created message.</returns>
    public static MailMessage Create(
        Guid accountId,
        Guid folderId,
        uint uid,
        string subject,
        string fromName,
        string fromAddress,
        string recipients,
        DateTimeOffset sentAt,
        string bodyText,
        string? bodyHtml = null,
        string? messageIdHeader = null,
        bool isRead = false)
    {
        if (accountId == Guid.Empty)
        {
            throw new ArgumentException("Account id is required.", nameof(accountId));
        }

        if (folderId == Guid.Empty)
        {
            throw new ArgumentException("Folder id is required.", nameof(folderId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(fromAddress);

        return new MailMessage
        {
            AccountId = accountId,
            FolderId = folderId,
            Uid = uid,
            Subject = subject ?? string.Empty,
            FromName = fromName ?? string.Empty,
            FromAddress = fromAddress,
            Recipients = recipients ?? string.Empty,
            SentAt = sentAt,
            BodyText = bodyText ?? string.Empty,
            BodyHtml = bodyHtml,
            MessageIdHeader = messageIdHeader,
            IsRead = isRead,
            Preview = BuildPreview(bodyText),
        };
    }

    /// <summary>
    /// Sets the read state.
    /// </summary>
    /// <param name="isRead">The new state.</param>
    public void SetRead(bool isRead)
    {
        IsRead = isRead;
    }

    /// <summary>
    /// Sets the flagged state.
    /// </summary>
    /// <param name="isFlagged">The new state.</param>
    public void SetFlagged(bool isFlagged)
    {
        IsFlagged = isFlagged;
    }

    /// <summary>
    /// Adds attachment metadata to the message.
    /// </summary>
    /// <param name="fileName">The file name.</param>
    /// <param name="contentType">The MIME content type.</param>
    /// <param name="size">The size in bytes.</param>
    public void AddAttachment(string fileName, string contentType, long size)
    {
        _attachments.Add(MailAttachment.Create(Id, _attachments.Count, fileName, contentType, size));
    }

    /// <summary>
    /// Builds a single-line preview by collapsing whitespace and truncating.
    /// </summary>
    /// <param name="text">The plain-text body.</param>
    /// <returns>The preview text.</returns>
    private static string BuildPreview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(PreviewLength);
        var previousWasSpace = false;
        foreach (var ch in text)
        {
            if (builder.Length >= PreviewLength)
            {
                break;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (!previousWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                previousWasSpace = true;
            }
            else
            {
                builder.Append(ch);
                previousWasSpace = false;
            }
        }

        return builder.ToString().TrimEnd();
    }
}
