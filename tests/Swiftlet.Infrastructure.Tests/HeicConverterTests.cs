using AwesomeAssertions;
using ImageMagick;
using Swiftlet.Infrastructure.Mail;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="HeicConverter"/> and HEIC handling in <see cref="InlineImageExtractor"/>.
/// </summary>
public class HeicConverterTests
{
    /// <summary>
    /// Verifies the HEIC MIME types are recognised and others are not.
    /// </summary>
    /// <param name="mimeType">The MIME type.</param>
    /// <param name="expected">Whether it is HEIC.</param>
    [Theory]
    [InlineData("image/heic", true)]
    [InlineData("IMAGE/HEIF", true)]
    [InlineData("image/png", false)]
    public void IsHeic_MimeType_ReturnsExpected(string mimeType, bool expected)
    {
        HeicConverter.IsHeic(mimeType).Should().Be(expected);
    }

    /// <summary>
    /// Verifies the bundled ImageMagick can read HEIC (writing is not supported, so no sample can be generated here).
    /// </summary>
    [Fact]
    public void SupportedFormats_Heic_CanBeRead()
    {
        MagickNET.SupportedFormats.Single(f => f.Format == MagickFormat.Heic).SupportsReading.Should().BeTrue();
    }

    /// <summary>
    /// Verifies undecodable data yields null instead of throwing.
    /// </summary>
    [Fact]
    public void ToJpeg_GarbageData_ReturnsNull()
    {
        HeicConverter.ToJpeg([1, 2, 3, 4]).Should().BeNull();
    }

    /// <summary>
    /// Verifies a HEIC part that cannot be decoded is left out while other images stay.
    /// </summary>
    [Fact]
    public void Extract_UndecodableHeic_IsSkippedAndOthersKept()
    {
        var builder = new MimeKit.BodyBuilder { HtmlBody = "<p>hi</p>" };
        builder.LinkedResources.Add("a.heic", [1, 2, 3], MimeKit.ContentType.Parse("image/heic")).ContentId = "a@x";
        builder.LinkedResources.Add("b.png", [4, 5], MimeKit.ContentType.Parse("image/png")).ContentId = "b@x";
        var message = new MimeKit.MimeMessage { Body = builder.ToMessageBody() };

        var images = InlineImageExtractor.Extract(message, ["a@x", "b@x"]);

        images.Should().ContainSingle().Which.Key.Should().Be("b@x");
    }

    /// <summary>
    /// Verifies a generic binary part is treated as HEIC only when its file name says so.
    /// </summary>
    /// <param name="mimeType">The MIME type.</param>
    /// <param name="fileName">The file name.</param>
    /// <param name="expected">Whether it is HEIC.</param>
    [Theory]
    [InlineData("application/octet-stream", "IMG_6686.HEIC", true)]
    [InlineData("application/octet-stream", "a.png", false)]
    [InlineData("application/octet-stream", null, false)]
    [InlineData("image/png", "a.heic", false)]
    public void IsHeicFile_NameAndType_ReturnsExpected(string mimeType, string? fileName, bool expected)
    {
        HeicConverter.IsHeicFile(mimeType, fileName).Should().Be(expected);
    }

    /// <summary>
    /// Verifies an image over the 2 MB limit is scaled down to a smaller JPEG instead of being dropped.
    /// </summary>
    [Fact]
    public void Extract_OversizedJpeg_IsScaledDown()
    {
        using var noise = new MagickImage("xc:", 2200, 2200);
        noise.AddNoise(NoiseType.Random);
        noise.Format = MagickFormat.Png;
        var big = noise.ToByteArray();
        big.Length.Should().BeGreaterThan(2 * 1024 * 1024);
        var builder = new MimeKit.BodyBuilder { HtmlBody = "<p>hi</p>" };
        builder.LinkedResources.Add("p.heic", big, MimeKit.ContentType.Parse("image/jpeg")).ContentId = "p@x";
        var message = new MimeKit.MimeMessage { Body = builder.ToMessageBody() };

        var images = InlineImageExtractor.Extract(message, ["p@x"]);

        var image = images.Should().ContainSingle().Which.Value;
        image.ContentType.Should().Be("image/jpeg");
        image.Content.Length.Should().BeLessThan(big.Length);
        using var decoded = new MagickImage(image.Content.ToArray());
        decoded.Width.Should().BeLessThanOrEqualTo(2048);
    }
}
