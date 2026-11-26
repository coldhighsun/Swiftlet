using System.Text;
using Swiftlet.Domain.Labels;

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
    /// Backing field of <see cref="Labels"/>.
    /// </summary>
    private readonly List<Label> _labels = [];

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
    /// Gets a value indicating whether a local read or flagged change has not yet been written to the server.
    /// </summary>
    public bool IsFlagSyncPending { get; private set; }

    /// <summary>
    /// Gets the attachments.
    /// </summary>
    public IReadOnlyList<MailAttachment> Attachments => _attachments;

    /// <summary>
    /// Gets the labels attached to the message.
    /// </summary>
    public IReadOnlyList<Label> Labels => _labels;

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
    /// Re-homes the cached message after the server moved it to another folder of the same account, so its content
    /// need not be downloaded again. Labels, flags and attachments stay as they are.
    /// </summary>
    /// <param name="folderId">The folder the message now belongs to.</param>
    /// <param name="uid">The IMAP UID the message has in that folder.</param>
    public void MoveToFolder(Guid folderId, uint uid)
    {
        if (folderId == Guid.Empty)
        {
            throw new ArgumentException("Folder id is required.", nameof(folderId));
        }

        FolderId = folderId;
        Uid = uid;
    }

    /// <summary>
    /// Creates a copy of this message for another folder of the same account, used when the server holds the same message
    /// in two folders (Gmail's All Mail) so its content need not be downloaded twice. Labels and the starred state are not copied.
    /// </summary>
    /// <param name="folderId">The folder the copy belongs to.</param>
    /// <param name="uid">The IMAP UID of the message in that folder.</param>
    /// <param name="isRead">Whether the message is read on the server.</param>
    /// <returns>The copy, including its attachment metadata.</returns>
    public MailMessage CopyToFolder(Guid folderId, uint uid, bool isRead)
    {
        var copy = Create(
            AccountId,
            folderId,
            uid,
            Subject,
            FromName,
            FromAddress,
            Recipients,
            SentAt,
            BodyText,
            BodyHtml,
            MessageIdHeader,
            isRead);
        foreach (var attachment in _attachments.OrderBy(a => a.Ordinal))
        {
            copy.AddAttachment(attachment.FileName, attachment.ContentType, attachment.Size);
        }

        return copy;
    }

    /// <summary>
    /// Sets the read state.
    /// </summary>
    /// <param name="isRead">The new state.</param>
    public void SetRead(bool isRead)
    {
        if (IsRead == isRead)
        {
            return;
        }

        IsRead = isRead;
        IsFlagSyncPending = true;
    }

    /// <summary>
    /// Sets the flagged state.
    /// </summary>
    /// <param name="isFlagged">The new state.</param>
    public void SetFlagged(bool isFlagged)
    {
        if (IsFlagged == isFlagged)
        {
            return;
        }

        IsFlagged = isFlagged;
        IsFlagSyncPending = true;
    }

    /// <summary>
    /// Clears the pending marker after the local flags were written to the server.
    /// </summary>
    public void MarkFlagsSynced()
    {
        IsFlagSyncPending = false;
    }

    /// <summary>
    /// Copies the server's flags unless a local change is still waiting to be pushed.
    /// </summary>
    /// <param name="isRead">The server's read state.</param>
    /// <param name="isFlagged">The server's flagged state.</param>
    /// <returns>True if the local values changed.</returns>
    public bool ApplyServerFlags(bool isRead, bool isFlagged)
    {
        if (IsFlagSyncPending)
        {
            return false;
        }

        if (IsRead == isRead && IsFlagged == isFlagged)
        {
            return false;
        }

        IsRead = isRead;
        IsFlagged = isFlagged;
        return true;
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
    /// Attaches a label; does nothing if it is already attached.
    /// </summary>
    /// <param name="label">The label to attach.</param>
    public void AddLabel(Label label)
    {
        ArgumentNullException.ThrowIfNull(label);
        if (_labels.Exists(existing => existing.Id == label.Id))
        {
            return;
        }

        _labels.Add(label);
    }

    /// <summary>
    /// Replaces the attached labels, used by persistence after it loaded the label links from the global store.
    /// </summary>
    /// <param name="labels">The labels now attached to the message.</param>
    public void SetLabels(IEnumerable<Label> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);
        _labels.Clear();
        _labels.AddRange(labels);
    }

    /// <summary>
    /// Detaches a label; does nothing if it is not attached.
    /// </summary>
    /// <param name="labelId">The label identifier.</param>
    public void RemoveLabel(Guid labelId)
    {
        _labels.RemoveAll(existing => existing.Id == labelId);
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
