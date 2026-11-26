using AwesomeAssertions;
using MimeKit;
using Swiftlet.Infrastructure.Mail;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="InlineImageExtractor"/>.
/// </summary>
public class InlineImageExtractorTests
{
    /// <summary>
    /// Builds a message whose HTML body has the given linked resources.
    /// </summary>
    /// <param name="resources">The resources as (Content-ID, content type, bytes).</param>
    /// <returns>The message.</returns>
    private static MimeMessage Build(params (string ContentId, string ContentType, byte[] Bytes)[] resources)
    {
        var builder = new BodyBuilder { HtmlBody = "<p>hi</p>" };
        foreach (var (contentId, contentType, bytes) in resources)
        {
            var part = builder.LinkedResources.Add($"{contentId}.bin", bytes, ContentType.Parse(contentType));
            part.ContentId = contentId;
        }

        return new MimeMessage { Body = builder.ToMessageBody() };
    }

    /// <summary>
    /// Verifies only the wanted images are returned, keyed by the normalized identifier.
    /// </summary>
    [Fact]
    public void Extract_WantedAndUnwantedImages_ReturnsOnlyWanted()
    {
        var message = Build(("logo@x", "image/png", [1, 2, 3]), ("other@x", "image/png", [9]));

        var images = InlineImageExtractor.Extract(message, ["logo@x"]);

        images.Should().ContainSingle().Which.Key.Should().Be("logo@x");
        images["logo@x"].ContentType.Should().Be("image/png");
        images["logo@x"].Content.ToArray().Should().Equal(1, 2, 3);
    }

    /// <summary>
    /// Verifies the identifier comparison ignores case and angle brackets.
    /// </summary>
    [Fact]
    public void Extract_IdentifierWithDifferentCase_StillMatches()
    {
        var message = Build(("Logo@Example.COM", "image/jpeg", [5]));

        var images = InlineImageExtractor.Extract(message, ["logo@example.com"]);

        images.Should().ContainKey("logo@example.com");
        images["logo@example.com"].ContentType.Should().Be("image/jpeg");
    }

    /// <summary>
    /// Verifies SVG and non-image parts are never returned.
    /// </summary>
    /// <param name="contentType">The part's content type.</param>
    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("application/pdf")]
    [InlineData("text/html")]
    public void Extract_UnsafeOrNonImageType_IsSkipped(string contentType)
    {
        var message = Build(("a@x", contentType, [1, 2]));

        InlineImageExtractor.Extract(message, ["a@x"]).Should().BeEmpty();
    }

    /// <summary>
    /// Verifies an image larger than the single-image limit is skipped while smaller ones are kept.
    /// </summary>
    [Fact]
    public void Extract_ImageOverSingleLimit_IsSkipped()
    {
        var message = Build(("big@x", "image/png", new byte[(2 * 1024 * 1024) + 1]), ("small@x", "image/png", [1]));

        var images = InlineImageExtractor.Extract(message, ["big@x", "small@x"]);

        images.Keys.Should().Equal("small@x");
    }

    /// <summary>
    /// Verifies images stop being taken once the total limit would be exceeded.
    /// </summary>
    [Fact]
    public void Extract_TotalOverLimit_SkipsTheExcess()
    {
        var oneMegabyteTwice = new byte[1024 * 1024 * 2];
        var message = Build(
            ("a@x", "image/png", oneMegabyteTwice),
            ("b@x", "image/png", oneMegabyteTwice),
            ("c@x", "image/png", oneMegabyteTwice),
            ("d@x", "image/png", oneMegabyteTwice),
            ("e@x", "image/png", oneMegabyteTwice),
            ("f@x", "image/png", oneMegabyteTwice));

        var images = InlineImageExtractor.Extract(message, ["a@x", "b@x", "c@x", "d@x", "e@x", "f@x"]);

        images.Should().HaveCount(5);
    }

    /// <summary>
    /// Verifies a message without embedded images yields nothing.
    /// </summary>
    [Fact]
    public void Extract_MessageWithoutParts_ReturnsEmpty()
    {
        var message = new MimeMessage { Body = new TextPart("html") { Text = "<p>x</p>" } };

        InlineImageExtractor.Extract(message, ["a@x"]).Should().BeEmpty();
    }
}
