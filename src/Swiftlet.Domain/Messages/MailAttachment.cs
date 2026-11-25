namespace Swiftlet.Domain.Messages;

/// <summary>
/// Metadata of a message attachment.
/// </summary>
public sealed class MailAttachment
{
    /// <summary>
    /// Gets the unique identifier of the attachment.
    /// </summary>
    public Guid Id { get; private set; } = Guid.NewGuid();

    /// <summary>
    /// Gets the identifier of the owning message.
    /// </summary>
    public Guid MessageId { get; private set; }

    /// <summary>
    /// Gets the file name.
    /// </summary>
    public string FileName { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the MIME content type.
    /// </summary>
    public string ContentType { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the size in bytes.
    /// </summary>
    public long Size { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="MailAttachment"/> class for persistence.
    /// </summary>
    private MailAttachment()
    {
    }

    /// <summary>
    /// Creates attachment metadata.
    /// </summary>
    /// <param name="messageId">The owning message.</param>
    /// <param name="fileName">The file name.</param>
    /// <param name="contentType">The MIME content type.</param>
    /// <param name="size">The size in bytes.</param>
    /// <returns>The created attachment.</returns>
    internal static MailAttachment Create(Guid messageId, string fileName, string contentType, long size)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentOutOfRangeException.ThrowIfNegative(size);

        return new MailAttachment
        {
            MessageId = messageId,
            FileName = fileName,
            ContentType = contentType,
            Size = size,
        };
    }
}
