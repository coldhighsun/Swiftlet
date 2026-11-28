using System.IO.Compression;
using System.Text;
using AwesomeAssertions;
using SkiaSharp;
using Swiftlet.Application.Abstractions;
using Swiftlet.Infrastructure.Mail;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="AttachmentPreviewer"/>.
/// </summary>
public class AttachmentPreviewerTests
{
    /// <summary>
    /// The previewer under test.
    /// </summary>
    private readonly AttachmentPreviewer _previewer = new();

    /// <summary>
    /// Builds a zip archive from named entries.
    /// </summary>
    /// <param name="entries">The entry names and contents.</param>
    /// <returns>The archive bytes.</returns>
    private static byte[] Zip(params (string Name, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(content);
            }
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Verifies the types that can be previewed are recognised by extension or content type.
    /// </summary>
    /// <param name="fileName">The file name.</param>
    /// <param name="contentType">The content type.</param>
    /// <param name="expected">Whether a preview is possible.</param>
    [Theory]
    [InlineData("photo.PNG", "application/octet-stream", true)]
    [InlineData("scan", "image/webp", true)]
    [InlineData("scan", "image/tiff", false)]
    [InlineData("IMG_1.HEIC", "image/heic", false)]
    [InlineData("logo.svg", "image/svg+xml", false)]
    [InlineData("notes.txt", "application/octet-stream", true)]
    [InlineData("data", "application/json", true)]
    [InlineData("backup.zip", "application/octet-stream", true)]
    [InlineData("letter.docx", "application/octet-stream", true)]
    [InlineData("setup.exe", "application/octet-stream", false)]
    [InlineData("movie.mp4", "video/mp4", false)]
    [InlineData("report.pdf", "application/pdf", false)]
    public void CanPreview_FileType_ReturnsExpected(string fileName, string contentType, bool expected)
    {
        _previewer.CanPreview(fileName, contentType).Should().Be(expected);
    }

    /// <summary>
    /// Verifies a large image is scaled down to the thumbnail size, keeping its proportions.
    /// </summary>
    [Fact]
    public void Create_LargeImage_ReturnsScaledThumbnail()
    {
        var content = TestImages.Solid(1920, 1080, SKEncodedImageFormat.Jpeg);

        var preview = _previewer.Create("big.jpg", "image/jpeg", content);

        preview.Kind.Should().Be(AttachmentPreviewKind.Image);
        using var thumbnail = SKBitmap.Decode(preview.Image);
        thumbnail.Width.Should().Be(AttachmentPreviewer.MaxThumbnailSide);
        thumbnail.Height.Should().Be(270);
    }

    /// <summary>
    /// Verifies a small image keeps its size.
    /// </summary>
    [Fact]
    public void Create_SmallImage_IsNotEnlarged()
    {
        var content = TestImages.Solid(40, 20, SKEncodedImageFormat.Png);

        var preview = _previewer.Create("icon.png", "image/png", content);

        using var thumbnail = SKBitmap.Decode(preview.Image);
        thumbnail.Width.Should().Be(40);
        thumbnail.Height.Should().Be(20);
    }

    /// <summary>
    /// Verifies a photo taken with a rotated camera is shown upright.
    /// </summary>
    [Fact]
    public void Create_RotatedPhoto_AppliesExifOrientation()
    {
        var content = TestImages.WithOrientation(TestImages.Solid(80, 40, SKEncodedImageFormat.Jpeg), 6);

        var preview = _previewer.Create("photo.jpg", "image/jpeg", content);

        using var thumbnail = SKBitmap.Decode(preview.Image);
        thumbnail.Width.Should().Be(40);
        thumbnail.Height.Should().Be(80);
    }

    /// <summary>
    /// Verifies a transparent image stays transparent.
    /// </summary>
    [Fact]
    public void Create_TransparentImage_ReturnsPng()
    {
        var content = TestImages.Solid(600, 300, SKEncodedImageFormat.Png, SKColors.Transparent);

        var preview = _previewer.Create("logo.png", "image/png", content);

        preview.Image.Should().StartWith(new byte[] { 0x89, 0x50, 0x4E, 0x47 });
        using var thumbnail = SKBitmap.Decode(preview.Image);
        thumbnail.GetPixel(0, 0).Alpha.Should().Be(0);
    }

    /// <summary>
    /// Verifies an image whose header declares too many pixels is refused before it is decoded.
    /// </summary>
    [Fact]
    public void Create_ImageWithTooManyPixels_ReturnsUnsupported()
    {
        var content = TestImages.PngHeader(20_000, 20_000);

        var preview = _previewer.Create("bomb.png", "image/png", content);

        preview.Kind.Should().Be(AttachmentPreviewKind.Unsupported);
    }

    /// <summary>
    /// Verifies bytes that are not an image give no preview instead of an exception.
    /// </summary>
    [Fact]
    public void Create_CorruptImage_ReturnsUnsupported()
    {
        var preview = _previewer.Create("broken.png", "image/png", new byte[] { 1, 2, 3, 4, 5 });

        preview.Kind.Should().Be(AttachmentPreviewKind.Unsupported);
    }

    /// <summary>
    /// Verifies UTF-8 text is shown.
    /// </summary>
    [Fact]
    public void Create_Utf8Text_ReturnsText()
    {
        var content = Encoding.UTF8.GetBytes("你好，世界\r\nsecond line");

        var preview = _previewer.Create("note.txt", "text/plain", content);

        preview.Kind.Should().Be(AttachmentPreviewKind.Text);
        preview.Text.Should().Be("你好，世界\nsecond line");
        preview.Truncated.Should().BeFalse();
    }

    /// <summary>
    /// Verifies GBK text without any declared charset is decoded.
    /// </summary>
    [Fact]
    public void Create_GbkText_IsDecoded()
    {
        MailEncodings.Register();
        var content = Encoding.GetEncoding("gbk").GetBytes("这是一个用于测试的中文文本文件");

        var preview = _previewer.Create("note.txt", "application/octet-stream", content);

        preview.Text.Should().Be("这是一个用于测试的中文文本文件");
    }

    /// <summary>
    /// Verifies only the beginning of a long text is shown.
    /// </summary>
    [Fact]
    public void Create_LongText_IsTruncatedToLines()
    {
        var content = Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Range(1, 500).Select(i => $"line {i}")));

        var preview = _previewer.Create("big.log", "text/plain", content);

        preview.Truncated.Should().BeTrue();
        preview.Text!.Split('\n').Should().HaveCount(40);
        preview.Text.Should().StartWith("line 1\n");
    }

    /// <summary>
    /// Verifies a file with NUL bytes is treated as binary.
    /// </summary>
    [Fact]
    public void Create_BinaryNamedAsText_ReturnsUnsupported()
    {
        var content = new byte[] { 0x4D, 0x5A, 0x00, 0x00, 0x03, 0x00 };

        var preview = _previewer.Create("data.txt", "text/plain", content);

        preview.Kind.Should().Be(AttachmentPreviewKind.Unsupported);
    }

    /// <summary>
    /// Verifies the entries of an archive are listed, and the rest is counted.
    /// </summary>
    [Fact]
    public void Create_ZipWithManyEntries_ListsFirstThirty()
    {
        var entries = Enumerable.Range(1, 35).Select(i => ($"folder/file{i}.txt", "content")).ToArray();

        var preview = _previewer.Create("archive.zip", "application/zip", Zip(entries));

        preview.Kind.Should().Be(AttachmentPreviewKind.Listing);
        preview.Text!.Split('\n').Should().HaveCount(30);
        preview.Text.Should().StartWith("folder/file1.txt  (7 B)");
        preview.MoreEntries.Should().Be(5);
    }

    /// <summary>
    /// Verifies a damaged archive gives no preview.
    /// </summary>
    [Fact]
    public void Create_CorruptZip_ReturnsUnsupported()
    {
        var preview = _previewer.Create("archive.zip", "application/zip", new byte[] { 1, 2, 3, 4 });

        preview.Kind.Should().Be(AttachmentPreviewKind.Unsupported);
    }

    /// <summary>
    /// Verifies the text of a Word document is extracted paragraph by paragraph.
    /// </summary>
    [Fact]
    public void Create_Docx_ReturnsParagraphText()
    {
        const string xml = "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>"
            + "<w:p><w:r><w:t>First paragraph</w:t></w:r></w:p><w:p><w:r><w:t>Second</w:t></w:r></w:p></w:body></w:document>";

        var preview = _previewer.Create("letter.docx", "application/octet-stream", Zip(("word/document.xml", xml)));

        preview.Kind.Should().Be(AttachmentPreviewKind.Text);
        preview.Text.Should().Be("First paragraph\nSecond");
    }

    /// <summary>
    /// Verifies the shared strings of a spreadsheet are shown.
    /// </summary>
    [Fact]
    public void Create_Xlsx_ReturnsSharedStrings()
    {
        const string xml = "<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
            + "<si><t>Name</t></si><si><t>Total</t></si></sst>";

        var preview = _previewer.Create("sheet.xlsx", "application/octet-stream", Zip(("xl/sharedStrings.xml", xml)));

        preview.Text.Should().Be("Name\nTotal");
    }

    /// <summary>
    /// Verifies a document with a DTD is refused instead of being expanded.
    /// </summary>
    [Fact]
    public void Create_DocxWithDoctype_ReturnsUnsupported()
    {
        const string xml = "<!DOCTYPE d [<!ENTITY a \"aaaaaaaaaa\">]><d>&a;&a;</d>";

        var preview = _previewer.Create("evil.docx", "application/octet-stream", Zip(("word/document.xml", xml)));

        preview.Kind.Should().Be(AttachmentPreviewKind.Unsupported);
    }

    /// <summary>
    /// Verifies a very long document part gives only the first part of the text.
    /// </summary>
    [Fact]
    public void Create_DocxWithHugeText_IsTruncated()
    {
        var xml = "<d><p>" + new string('a', 200_000) + "</p></d>";

        var preview = _previewer.Create("huge.docx", "application/octet-stream", Zip(("word/document.xml", xml)));

        preview.Truncated.Should().BeTrue();
        preview.Text!.Length.Should().Be(800);
    }

    /// <summary>
    /// Verifies a document without the expected part gives no preview.
    /// </summary>
    [Fact]
    public void Create_DocxWithoutDocumentPart_ReturnsUnsupported()
    {
        var preview = _previewer.Create("odd.docx", "application/octet-stream", Zip(("other.xml", "<a/>")));

        preview.Kind.Should().Be(AttachmentPreviewKind.Unsupported);
    }
}
