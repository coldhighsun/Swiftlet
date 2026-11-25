using AwesomeAssertions;
using Swiftlet.Desktop.Services;
using Swiftlet.Desktop.ViewModels;
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
