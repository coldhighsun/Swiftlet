using AwesomeAssertions;
using Swiftlet.Application.Abstractions;
using Swiftlet.Desktop.Formatting;
using Swiftlet.Desktop.Html;
using Swiftlet.Desktop.ViewModels.Compose;
using Swiftlet.Domain.Folders;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests for formatting helpers and compose templates.
/// </summary>
public class HelperTests
{
    /// <summary>
    /// Verifies byte counts are formatted with the largest sensible unit.
    /// </summary>
    /// <param name="bytes">The size in bytes.</param>
    /// <param name="expected">The expected text.</param>
    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(5L * 1024 * 1024, "5 MB")]
    [InlineData(-5, "0 B")]
    public void Format_Bytes_ReturnsHumanReadableSize(long bytes, string expected)
    {
        FileSizeFormatter.Format(bytes).Should().Be(expected);
    }

    /// <summary>
    /// Verifies the HTML preview carries a policy that blocks scripts and remote content.
    /// </summary>
    [Fact]
    public void Build_Html_AddsRestrictiveContentSecurityPolicy()
    {
        var document = HtmlPreviewFile.Build("<p>Hi</p><img src=\"https://tracker.example/p.gif\">");

        document.Should().Contain("Content-Security-Policy").And.Contain("default-src 'none'").And.Contain("<p>Hi</p>");
        document.IndexOf("Content-Security-Policy", StringComparison.Ordinal)
            .Should().BeLessThan(document.IndexOf("<p>Hi</p>", StringComparison.Ordinal));
    }

    /// <summary>
    /// Verifies the embedded-view document blocks scripts and remote content, is UTF-8 and keeps the body after the policy.
    /// </summary>
    [Fact]
    public void Build_MailDocument_AddsPolicyCharsetAndStyleBeforeBody()
    {
        var document = HtmlMailDocument.Build("<p>Hi</p><script>alert(1)</script>");

        document.Should().Contain("<meta charset=\"utf-8\">")
            .And.Contain("default-src 'none'")
            .And.Contain("img-src data:")
            .And.Contain("<style>")
            .And.Contain("<p>Hi</p>");
        document.IndexOf("Content-Security-Policy", StringComparison.Ordinal)
            .Should().BeLessThan(document.IndexOf("<p>Hi</p>", StringComparison.Ordinal));
    }

    /// <summary>
    /// Verifies only web and mail links may be opened outside the embedded view.
    /// </summary>
    /// <param name="uri">The link target.</param>
    /// <param name="expected">Whether the link may leave the view.</param>
    [Theory]
    [InlineData("https://example.com/a", true)]
    [InlineData("http://example.com", true)]
    [InlineData("mailto:a@example.com", true)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("about:blank", false)]
    [InlineData("data:text/html,hi", false)]
    public void IsExternalLink_Scheme_AllowsOnlyWebAndMail(string uri, bool expected)
    {
        HtmlMailDocument.IsExternalLink(new Uri(uri)).Should().Be(expected);
    }

    /// <summary>
    /// Verifies a missing link target is not external.
    /// </summary>
    [Fact]
    public void IsExternalLink_Null_ReturnsFalse()
    {
        HtmlMailDocument.IsExternalLink(null).Should().BeFalse();
    }

    /// <summary>
    /// Verifies remote images stay blocked by default and are allowed only for images when requested; scripts never are.
    /// </summary>
    [Fact]
    public void Build_RemoteImagesFlag_ChangesOnlyTheImagePolicy()
    {
        var blocked = HtmlMailDocument.Build("<p>x</p>");
        var allowed = HtmlMailDocument.Build("<p>x</p>", allowRemoteImages: true);

        blocked.Should().Contain("img-src data:;").And.NotContain("https:");
        allowed.Should().Contain("img-src data: https: http:").And.Contain("default-src 'none'").And.NotContain("script-src");
    }

    /// <summary>
    /// Verifies remote image references are detected in their different spellings.
    /// </summary>
    /// <param name="html">The HTML body.</param>
    [Theory]
    [InlineData("<img src=\"https://x.example/a.png\">")]
    [InlineData("<img alt=\"a\" src='http://x.example/a.png'>")]
    [InlineData("<IMG SRC=https://x.example/a.png>")]
    [InlineData("<td background=\"https://x.example/bg.png\">")]
    [InlineData("<div style=\"background:url(https://x.example/bg.png)\">")]
    [InlineData("<div style=\"background:url( 'http://x.example/bg.png' )\">")]
    public void HasRemoteImages_RemoteReference_ReturnsTrue(string html)
    {
        HtmlMailDocument.HasRemoteImages(html).Should().BeTrue();
    }

    /// <summary>
    /// Verifies local, embedded and missing image references are not reported as remote.
    /// </summary>
    /// <param name="html">The HTML body.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<p>text only</p>")]
    [InlineData("<img src=\"cid:logo\">")]
    [InlineData("<img src=\"data:image/png;base64,AAAA\">")]
    [InlineData("<img src=\"images/a.png\">")]
    [InlineData("<a href=\"https://x.example\">link</a>")]
    public void HasRemoteImages_NoRemoteImage_ReturnsFalse(string? html)
    {
        HtmlMailDocument.HasRemoteImages(html).Should().BeFalse();
    }

    /// <summary>
    /// Verifies embedded images replace their cid references with data URIs of the right type, in any spelling.
    /// </summary>
    [Fact]
    public void Build_InlineImages_ReplacesCidReferencesWithDataUris()
    {
        var images = new Dictionary<string, InlineImage>
        {
            ["logo@example.com"] = new("image/png", new byte[] { 1, 2, 3 }),
            ["photo"] = new("image/jpeg", new byte[] { 4 }),
        };
        const string html = "<img src=\"cid:LOGO@example.com\"><img src=\"cid:photo\"><img src=\"cid:unknown\"><img src='cid:%70hoto'>";

        var document = HtmlMailDocument.Build(html, inlineImages: images);

        document.Should().Contain("src=\"data:image/png;base64,AQID\"")
            .And.Contain("src=\"data:image/jpeg;base64,BA==\"")
            .And.Contain("src=\"cid:unknown\"")
            .And.Contain("src='data:image/jpeg;base64,BA=='");
    }

    /// <summary>
    /// Verifies reply and forward subjects are not prefixed twice.
    /// </summary>
    /// <param name="subject">The original subject.</param>
    /// <param name="replySubject">The expected reply subject.</param>
    /// <param name="forwardSubject">The expected forward subject.</param>
    [Theory]
    [InlineData("Plan", "Re: Plan", "Fwd: Plan")]
    [InlineData("Re: Plan", "Re: Plan", "Fwd: Re: Plan")]
    [InlineData("RE: Plan", "RE: Plan", "Fwd: RE: Plan")]
    [InlineData("Fwd: Plan", "Re: Fwd: Plan", "Fwd: Plan")]
    [InlineData("", "Re:", "Fwd:")]
    public void Templates_Subjects_AvoidDuplicatePrefixes(string subject, string replySubject, string forwardSubject)
    {
        var account = TestData.Account();
        var message = TestData.Message(account, TestData.Folder(account, "INBOX", FolderRole.Inbox), 1, subject);

        ComposeTemplates.Reply(message, TestLocalizer.Chinese()).Subject.Should().Be(replySubject);
        ComposeTemplates.Forward(message, TestLocalizer.Chinese()).Subject.Should().Be(forwardSubject);
    }

    /// <summary>
    /// Verifies every line of the original is quoted in a reply and the account is preserved.
    /// </summary>
    [Fact]
    public void Reply_MultilineBody_QuotesEveryLine()
    {
        var account = TestData.Account();
        var message = TestData.Message(account, TestData.Folder(account, "INBOX", FolderRole.Inbox), 1);

        var request = ComposeTemplates.Reply(message, TestLocalizer.Chinese());

        request.Body.Should().Contain("> line one").And.Contain("> line two").And.Contain("Alice <alice@example.com>");
        request.AccountId.Should().Be(account.Id);
    }
}
