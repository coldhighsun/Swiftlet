namespace Swiftlet.Application.Abstractions;

/// <summary>
/// An attachment of an outgoing message.
/// </summary>
/// <param name="FileName">The file name.</param>
/// <param name="ContentType">The MIME content type.</param>
/// <param name="Content">The file content.</param>
public sealed record OutgoingAttachment(string FileName, string ContentType, ReadOnlyMemory<byte> Content);

/// <summary>
/// A message composed by the user, ready to be sent.
/// </summary>
public sealed class OutgoingMessage
{
    /// <summary>
    /// Gets the primary recipients.
    /// </summary>
    public IReadOnlyList<string> To { get; init; } = [];

    /// <summary>
    /// Gets the carbon-copy recipients.
    /// </summary>
    public IReadOnlyList<string> Cc { get; init; } = [];

    /// <summary>
    /// Gets the blind-carbon-copy recipients.
    /// </summary>
    public IReadOnlyList<string> Bcc { get; init; } = [];

    /// <summary>
    /// Gets the subject.
    /// </summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>
    /// Gets the plain-text body.
    /// </summary>
    public string TextBody { get; init; } = string.Empty;

    /// <summary>
    /// Gets the HTML body, if any.
    /// </summary>
    public string? HtmlBody { get; init; }

    /// <summary>
    /// Gets the attachments.
    /// </summary>
    public IReadOnlyList<OutgoingAttachment> Attachments { get; init; } = [];

    /// <summary>
    /// Gets the Message-ID of the message being replied to, if any.
    /// </summary>
    public string? InReplyTo { get; init; }
}
