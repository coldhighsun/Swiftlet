using AwesomeAssertions;
using MimeKit;
using SkiaSharp;
using Swiftlet.Infrastructure.Mail;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for how <see cref="InlineImageExtractor"/> treats formats and sizes.
/// </summary>
public class InlineImageConversionTests
{
    /// <summary>
    /// Builds a message embedding images.
    /// </summary>
    /// <param name="images">The file name, content type, content identifier and bytes of each image.</param>
    /// <returns>The message.</returns>
    private static MimeMessage Message(params (string Name, string Type, string Id, byte[] Content)[] images)
    {
        var builder = new BodyBuilder { HtmlBody = "<p>hi</p>" };
        foreach (var (name, type, id, content) in images)
        {
            builder.LinkedResources.Add(name, content, ContentType.Parse(type)).ContentId = id;
        }

        return new MimeMessage { Body = builder.ToMessageBody() };
    }

    /// <summary>
    /// Verifies HEIC photos, which cannot be displayed, are left out while other images stay.
    /// </summary>
    /// <param name="type">The content type of the HEIC part.</param>
    [Theory]
    [InlineData("image/heic")]
    [InlineData("application/octet-stream")]
    public void Extract_Heic_IsSkippedAndOthersKept(string type)
    {
        var message = Message(("a.heic", type, "a@x", [1, 2, 3]), ("b.png", "image/png", "b@x", [4, 5]));

        var images = InlineImageExtractor.Extract(message, ["a@x", "b@x"]);

        images.Should().ContainSingle().Which.Key.Should().Be("b@x");
    }

    /// <summary>
    /// Verifies an image over the 2 MB limit is scaled down to a smaller JPEG instead of being dropped.
    /// </summary>
    [Fact]
    public void Extract_OversizedImage_IsScaledDown()
    {
        var big = TestImages.Noise(2200);
        big.Length.Should().BeGreaterThan(2 * 1024 * 1024);

        var images = InlineImageExtractor.Extract(Message(("p.png", "image/png", "p@x", big)), ["p@x"]);

        var image = images.Should().ContainSingle().Which.Value;
        image.ContentType.Should().Be("image/jpeg");
        image.Content.Length.Should().BeLessThan(big.Length);
        using var decoded = SKBitmap.Decode(image.Content.ToArray());
        decoded.Width.Should().Be(2048);
        decoded.Height.Should().Be(2048);
    }

    /// <summary>
    /// Verifies an oversized image that cannot be decoded is left out.
    /// </summary>
    [Fact]
    public void Extract_OversizedGarbage_IsSkipped()
    {
        var garbage = new byte[3 * 1024 * 1024];

        var images = InlineImageExtractor.Extract(Message(("p.jpg", "image/jpeg", "p@x", garbage)), ["p@x"]);

        images.Should().BeEmpty();
    }
}
