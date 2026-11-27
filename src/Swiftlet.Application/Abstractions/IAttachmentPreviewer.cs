namespace Swiftlet.Application.Abstractions;

/// <summary>
/// What kind of preview an attachment produced.
/// </summary>
public enum AttachmentPreviewKind
{
    /// <summary>
    /// A thumbnail image.
    /// </summary>
    Image,

    /// <summary>
    /// The beginning of a text document.
    /// </summary>
    Text,

    /// <summary>
    /// A list of the entries of an archive.
    /// </summary>
    Listing,

    /// <summary>
    /// The attachment cannot be previewed.
    /// </summary>
    Unsupported,

    /// <summary>
    /// The attachment is too large to download for a preview.
    /// </summary>
    TooLarge,
}

/// <summary>
/// A quick preview of an attachment.
/// </summary>
/// <param name="Kind">What the preview holds.</param>
/// <param name="Image">The thumbnail bytes (PNG or JPEG) for <see cref="AttachmentPreviewKind.Image"/>.</param>
/// <param name="Text">The text or the entry list (one entry per line).</param>
/// <param name="Truncated">Whether the text was cut short.</param>
/// <param name="MoreEntries">How many archive entries are not listed.</param>
public sealed record AttachmentPreview(
    AttachmentPreviewKind Kind,
    byte[]? Image = null,
    string? Text = null,
    bool Truncated = false,
    int MoreEntries = 0)
{
    /// <summary>
    /// Gets a preview that says the attachment cannot be previewed.
    /// </summary>
    public static AttachmentPreview Unsupported { get; } = new(AttachmentPreviewKind.Unsupported);

    /// <summary>
    /// Gets a preview that says the attachment is too large.
    /// </summary>
    public static AttachmentPreview TooLarge { get; } = new(AttachmentPreviewKind.TooLarge);
}

/// <summary>
/// Builds a quick preview from the content of an attachment.
/// </summary>
public interface IAttachmentPreviewer
{
    /// <summary>
    /// Gets a value indicating whether a preview can be built for a file, judged by its name and type only.
    /// </summary>
    /// <param name="fileName">The file name.</param>
    /// <param name="contentType">The MIME content type.</param>
    /// <returns><see langword="true"/> when the file is worth downloading for a preview.</returns>
    bool CanPreview(string fileName, string contentType);

    /// <summary>
    /// Builds the preview; failures to decode the content give <see cref="AttachmentPreview.Unsupported"/>.
    /// </summary>
    /// <param name="fileName">The file name.</param>
    /// <param name="contentType">The MIME content type.</param>
    /// <param name="content">The file content.</param>
    /// <returns>The preview.</returns>
    AttachmentPreview Create(string fileName, string contentType, ReadOnlyMemory<byte> content);
}
