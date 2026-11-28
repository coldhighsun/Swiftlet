using System.IO.Compression;
using System.Text;
using System.Xml;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Swiftlet.Application.Abstractions;

namespace Swiftlet.Infrastructure.Mail;

/// <summary>
/// Builds quick previews of attachments: thumbnails of images, the beginning of text files, the entries of archives
/// and the text of Office documents. Everything it reads is untrusted, so each step is bounded.
/// </summary>
/// <param name="logger">The logger that records why a preview failed; optional.</param>
public sealed class AttachmentPreviewer(ILogger<AttachmentPreviewer>? logger = null) : IAttachmentPreviewer
{
    /// <summary>
    /// The longest side of a thumbnail, in pixels.
    /// </summary>
    internal const int MaxThumbnailSide = 480;

    /// <summary>
    /// The number of bytes read from the start of a text file.
    /// </summary>
    private const int MaxTextBytes = 8 * 1024;

    /// <summary>
    /// The most lines of a text file that are shown.
    /// </summary>
    private const int MaxTextLines = 40;

    /// <summary>
    /// The most characters of a text file that are shown.
    /// </summary>
    private const int MaxTextChars = 2000;

    /// <summary>
    /// The most archive entries that are listed.
    /// </summary>
    private const int MaxListedEntries = 30;

    /// <summary>
    /// The most characters taken from an Office document.
    /// </summary>
    private const int MaxOfficeChars = 800;

    /// <summary>
    /// The largest Office part (uncompressed, as declared) that is opened.
    /// </summary>
    private const long MaxOfficePartBytes = 50L * 1024 * 1024;

    /// <summary>
    /// The logger.
    /// </summary>
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>
    /// Extensions of images that get a thumbnail.
    /// </summary>
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".jfif", ".gif", ".bmp", ".webp", ".ico",
    };

    /// <summary>
    /// Content types of images that get a thumbnail, for files whose extension says nothing.
    /// </summary>
    private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png", "image/jpeg", "image/jpg", "image/pjpeg", "image/gif", "image/bmp", "image/webp", "image/x-icon",
        "image/vnd.microsoft.icon",
    };

    /// <summary>
    /// Extensions of files that are shown as text.
    /// </summary>
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".log", ".json", ".xml", ".csv", ".tsv", ".yml", ".yaml", ".ini", ".cfg", ".conf",
        ".toml", ".properties", ".sql", ".sh", ".bat", ".cmd", ".ps1", ".py", ".js", ".ts", ".css", ".html", ".htm",
        ".cs", ".java", ".cpp", ".c", ".h", ".go", ".rs", ".rb", ".php", ".eml", ".ics", ".vcf", ".srt", ".csproj",
        ".sln", ".axaml", ".xaml", ".razor", ".vue", ".kt", ".swift", ".lua", ".r", ".tex", ".rtf",
    };

    /// <summary>
    /// Extensions of Office Open XML documents and the part that holds their text.
    /// </summary>
    private static readonly Dictionary<string, string> OfficeParts = new(StringComparer.OrdinalIgnoreCase)
    {
        [".docx"] = "word/document.xml",
        [".xlsx"] = "xl/sharedStrings.xml",
        [".pptx"] = "ppt/slides/slide1.xml",
    };

    /// <inheritdoc />
    public bool CanPreview(string fileName, string contentType)
    {
        return Classify(fileName, contentType) != FileKind.None;
    }

    /// <inheritdoc />
    public AttachmentPreview Create(string fileName, string contentType, ReadOnlyMemory<byte> content)
    {
        var kind = Classify(fileName, contentType);
        if (kind == FileKind.None || content.IsEmpty)
        {
            return AttachmentPreview.Unsupported;
        }

        try
        {
            return kind switch
            {
                FileKind.Image => CreateImage(content),
                FileKind.Text => CreateText(content),
                FileKind.Archive => CreateListing(content),
                _ => CreateOffice(fileName, content),
            };
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or IOException
            or NotSupportedException or ArgumentException)
        {
            // Corrupt, truncated or unsupported content is the normal reason; the preview then simply says so.
            _logger.LogDebug(ex, "Attachment preview of kind {Kind} failed", kind);
            return AttachmentPreview.Unsupported;
        }
    }

    /// <summary>
    /// Decides how a file is previewed, from its extension first and its content type second.
    /// </summary>
    /// <param name="fileName">The file name.</param>
    /// <param name="contentType">The MIME content type.</param>
    /// <returns>The kind of preview, or <see cref="FileKind.None"/>.</returns>
    private static FileKind Classify(string fileName, string contentType)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty);
        if (OfficeParts.ContainsKey(extension))
        {
            return FileKind.Office;
        }

        if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return FileKind.Archive;
        }

        if (ImageExtensions.Contains(extension))
        {
            return FileKind.Image;
        }

        if (TextExtensions.Contains(extension))
        {
            return FileKind.Text;
        }

        var type = contentType ?? string.Empty;
        if (ImageTypes.Contains(type))
        {
            return FileKind.Image;
        }

        if (type.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || type.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || type.Equals("application/xml", StringComparison.OrdinalIgnoreCase))
        {
            return FileKind.Text;
        }

        return type.Equals("application/zip", StringComparison.OrdinalIgnoreCase) ? FileKind.Archive : FileKind.None;
    }

    /// <summary>
    /// Decodes an image and scales it down to a thumbnail.
    /// </summary>
    /// <param name="content">The image bytes.</param>
    /// <returns>The preview.</returns>
    private static AttachmentPreview CreateImage(ReadOnlyMemory<byte> content)
    {
        // Transparency needs PNG; everything else is smaller as JPEG.
        return ImageScaler.Scale(content.Span, MaxThumbnailSide, 80, keepAlpha: true) is { } thumbnail
            ? new AttachmentPreview(AttachmentPreviewKind.Image, Image: thumbnail.Content)
            : AttachmentPreview.Unsupported;
    }

    /// <summary>
    /// Decodes the start of a text file.
    /// </summary>
    /// <param name="content">The file bytes.</param>
    /// <returns>The preview.</returns>
    private static AttachmentPreview CreateText(ReadOnlyMemory<byte> content)
    {
        var span = content.Span;
        var head = span[..Math.Min(span.Length, MaxTextBytes)];

        // A NUL byte never occurs in text of the supported encodings, so it marks a binary file.
        if (head.IndexOf((byte)0) >= 0)
        {
            return AttachmentPreview.Unsupported;
        }

        // Cutting at a byte count can split the last character, which decodes to U+FFFD.
        var text = MimeTextDecoder.Decode(head).TrimEnd('�');
        var truncated = span.Length > head.Length;
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length > MaxTextLines)
        {
            lines = lines[..MaxTextLines];
            truncated = true;
        }

        text = string.Join('\n', lines);
        if (text.Length > MaxTextChars)
        {
            text = text[..MaxTextChars];
            truncated = true;
        }

        return new AttachmentPreview(AttachmentPreviewKind.Text, Text: text.TrimEnd(), Truncated: truncated);
    }

    /// <summary>
    /// Lists the entries of a zip archive.
    /// </summary>
    /// <param name="content">The archive bytes.</param>
    /// <returns>The preview.</returns>
    private static AttachmentPreview CreateListing(ReadOnlyMemory<byte> content)
    {
        using var stream = new MemoryStream(content.ToArray(), writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entries = archive.Entries;
        var builder = new StringBuilder();
        foreach (var entry in entries.Take(MaxListedEntries))
        {
            builder.Append(entry.FullName);
            if (entry.Name.Length > 0)
            {
                builder.Append("  (").Append(FormatSize(entry.Length)).Append(')');
            }

            builder.Append('\n');
        }

        return new AttachmentPreview(
            AttachmentPreviewKind.Listing,
            Text: builder.ToString().TrimEnd(),
            MoreEntries: Math.Max(0, entries.Count - MaxListedEntries));
    }

    /// <summary>
    /// Reads the beginning of the text of a Word, Excel or PowerPoint document.
    /// </summary>
    /// <param name="fileName">The file name, which names the document type.</param>
    /// <param name="content">The document bytes.</param>
    /// <returns>The preview.</returns>
    private static AttachmentPreview CreateOffice(string fileName, ReadOnlyMemory<byte> content)
    {
        using var stream = new MemoryStream(content.ToArray(), writable: false);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = archive.GetEntry(OfficeParts[Path.GetExtension(fileName)]);
        if (entry is null || entry.Length > MaxOfficePartBytes)
        {
            return AttachmentPreview.Unsupported;
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxOfficePartBytes,
        };
        using var partStream = entry.Open();
        using var reader = XmlReader.Create(partStream, settings);
        var builder = new StringBuilder();
        var truncated = false;

        // Reading stops as soon as enough text was found, so a bomb inside the part is never fully inflated.
        while (reader.Read())
        {
            if (reader.NodeType is XmlNodeType.Text or XmlNodeType.SignificantWhitespace)
            {
                builder.Append(reader.Value);
            }
            else if (reader.NodeType == XmlNodeType.EndElement
                && reader.LocalName is "p" or "si"
                && builder.Length > 0 && builder[^1] != '\n')
            {
                builder.Append('\n');
            }

            if (builder.Length >= MaxOfficeChars)
            {
                truncated = true;
                break;
            }
        }

        var text = builder.ToString().Trim();
        if (text.Length == 0)
        {
            return AttachmentPreview.Unsupported;
        }

        if (text.Length > MaxOfficeChars)
        {
            text = text[..MaxOfficeChars];
        }

        return new AttachmentPreview(AttachmentPreviewKind.Text, Text: text, Truncated: truncated);
    }

    /// <summary>
    /// Formats a size in bytes for the archive listing.
    /// </summary>
    /// <param name="bytes">The size.</param>
    /// <returns>The readable size.</returns>
    private static string FormatSize(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
            _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
        };
    }

    /// <summary>
    /// How a file is previewed.
    /// </summary>
    private enum FileKind
    {
        /// <summary>
        /// No preview.
        /// </summary>
        None,

        /// <summary>
        /// A thumbnail.
        /// </summary>
        Image,

        /// <summary>
        /// The start of the text.
        /// </summary>
        Text,

        /// <summary>
        /// The entries of a zip archive.
        /// </summary>
        Archive,

        /// <summary>
        /// The text of an Office document.
        /// </summary>
        Office,
    }
}
