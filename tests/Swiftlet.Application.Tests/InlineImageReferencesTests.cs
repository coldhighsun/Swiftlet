using AwesomeAssertions;
using Swiftlet.Application.Queries;

namespace Swiftlet.Application.Tests;

/// <summary>
/// Tests for finding and rewriting <c>cid:</c> references in HTML mail.
/// </summary>
public class InlineImageReferencesTests
{
    /// <summary>
    /// Verifies content identifiers are decoded, unbracketed and case-folded.
    /// </summary>
    /// <param name="raw">The raw identifier.</param>
    /// <param name="expected">The normalized identifier.</param>
    [Theory]
    [InlineData("logo", "logo")]
    [InlineData("<Logo@Example.COM>", "logo@example.com")]
    [InlineData("  image%20one@x  ", "image one@x")]
    [InlineData("a%40b.com", "a@b.com")]
    public void Normalize_Identifier_ReturnsComparableForm(string raw, string expected)
    {
        InlineImageReferences.Normalize(raw).Should().Be(expected);
    }

    /// <summary>
    /// Verifies references are found in the common attribute and CSS forms, without duplicates.
    /// </summary>
    [Fact]
    public void Find_HtmlWithSeveralReferences_ReturnsDistinctNormalizedIds()
    {
        const string html = "<img src=\"cid:Logo\"><img src='cid:sig%40x'><td background=cid:bg><div style=\"background:url(cid:Logo)\"></div>"
            + "<img src=\"CID:logo\">";

        var ids = InlineImageReferences.Find(html);

        ids.Should().BeEquivalentTo(["logo", "sig@x", "bg"]);
    }

    /// <summary>
    /// Verifies null, blank and reference-free HTML produce no identifiers.
    /// </summary>
    /// <param name="html">The HTML body.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<p>no pictures</p>")]
    [InlineData("<img src=\"https://example.com/a.png\">")]
    [InlineData("<a href=\"mailto:a@example.com\">mail</a>")]
    public void Find_NoReferences_ReturnsEmpty(string? html)
    {
        InlineImageReferences.Find(html).Should().BeEmpty();
    }

    /// <summary>
    /// Verifies known references are replaced and unknown ones are kept as they were written.
    /// </summary>
    [Fact]
    public void Replace_KnownAndUnknownReferences_ReplacesOnlyKnown()
    {
        const string html = "<img src=\"cid:Logo\"><img src=\"cid:other\">";

        var result = InlineImageReferences.Replace(html, id => id == "logo" ? "data:image/png;base64,AAA" : null);

        result.Should().Be("<img src=\"data:image/png;base64,AAA\"><img src=\"cid:other\">");
    }
}
