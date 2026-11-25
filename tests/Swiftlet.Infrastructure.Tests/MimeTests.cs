using AwesomeAssertions;
using MimeKit;
using Swiftlet.Application.Abstractions;
using Swiftlet.Infrastructure.Mail;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for HTML conversion, MIME mapping and outgoing MIME building.
/// </summary>
public class MimeTests
{
    /// <summary>
    /// Verifies scripts and styles are dropped, line breaks kept and entities decoded.
    /// </summary>
    [Fact]
    public void Convert_HtmlWithScriptAndEntities_ReturnsReadableText()
    {
        const string html = "<style>p{color:red}</style><p>Hello&nbsp;<b>World</b> &amp; co</p><script>alert(1)</script><p>Second<br>line</p>";

        var text = HtmlToText.Convert(html);

        text.Should().Be("Hello World & co\nSecond\nline");
    }

    /// <summary>
    /// Verifies null and blank input produce an empty string.
    /// </summary>
    /// <param name="html">The input.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Convert_NullOrBlank_ReturnsEmpty(string? html)
    {
        HtmlToText.Convert(html).Should().BeEmpty();
    }

    /// <summary>
    /// Verifies a multipart message with text, HTML and an attachment is mapped completely.
    /// </summary>
    [Fact]
    public void Map_MultipartMessage_MapsAllFields()
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Alice", "alice@example.com"));
        message.To.Add(new MailboxAddress("Me", "me@example.com"));
        message.Cc.Add(new MailboxAddress("Bob", "bob@example.com"));
        message.Subject = "Report";
        message.MessageId = "abc@example.com";
        message.Date = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var body = new BodyBuilder { TextBody = "plain body", HtmlBody = "<p>html body</p>" };
        body.Attachments.Add("data.bin", new byte[] { 1, 2, 3, 4 }, ContentType.Parse("application/octet-stream"));
        message.Body = body.ToMessageBody();

        var mapped = MimeMessageMapper.Map(42, message, isRead: true, DateTimeOffset.MinValue);

        mapped.Uid.Should().Be(42u);
        mapped.Subject.Should().Be("Report");
        mapped.FromName.Should().Be("Alice");
        mapped.FromAddress.Should().Be("alice@example.com");
        mapped.Recipients.Should().Be("me@example.com, bob@example.com");
        mapped.SentAt.Should().Be(message.Date);
        mapped.BodyText.Should().Be("plain body");
        mapped.BodyHtml.Should().Be("<p>html body</p>");
        mapped.MessageId.Should().Be("abc@example.com");
        mapped.IsRead.Should().BeTrue();
        mapped.Attachments.Should().ContainSingle().Which.Should().Be(new RemoteAttachment("data.bin", "application/octet-stream", 4));
    }

    /// <summary>
    /// Verifies an HTML-only message gets a derived plain-text body.
    /// </summary>
    [Fact]
    public void Map_HtmlOnlyMessage_DerivesTextBody()
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("A", "a@example.com"));
        message.Body = new TextPart("html") { Text = "<p>Only <b>html</b></p>" };

        var mapped = MimeMessageMapper.Map(1, message, false, DateTimeOffset.MinValue);

        mapped.BodyText.Should().Be("Only html");
    }

    /// <summary>
    /// Verifies missing Date and From headers fall back to safe values.
    /// </summary>
    [Fact]
    public void Map_MissingDateAndFrom_UsesFallbacks()
    {
        var message = new MimeMessage { Body = new TextPart("plain") { Text = "x" } };
        message.Headers.Remove(HeaderId.Date);
        var fallback = new DateTimeOffset(2025, 12, 31, 0, 0, 0, TimeSpan.Zero);

        var mapped = MimeMessageMapper.Map(1, message, false, fallback);

        mapped.SentAt.Should().Be(fallback);
        mapped.FromAddress.Should().Be("unknown@invalid");
        mapped.Subject.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies the outgoing MIME carries sender, recipients, threading headers and attachments.
    /// </summary>
    [Fact]
    public void Build_FullMessage_SetsHeadersAndAttachments()
    {
        var account = TestData.Account("me@example.com");
        var outgoing = new OutgoingMessage
        {
            To = ["Bob <bob@example.com>"],
            Cc = ["carol@example.com"],
            Bcc = ["dave@example.com"],
            Subject = "Re: Plan",
            TextBody = "text",
            HtmlBody = "<p>html</p>",
            InReplyTo = "<orig@example.com>",
            Attachments = [new OutgoingAttachment("a.txt", "text/plain", new byte[] { 65, 66 })],
        };

        var mime = OutgoingMimeBuilder.Build(account, outgoing);

        mime.From.Mailboxes.Single().Address.Should().Be("me@example.com");
        mime.To.Mailboxes.Single().Name.Should().Be("Bob");
        mime.Cc.Mailboxes.Single().Address.Should().Be("carol@example.com");
        mime.Bcc.Mailboxes.Single().Address.Should().Be("dave@example.com");
        mime.InReplyTo.Should().Be("orig@example.com");
        mime.MessageId.Should().NotBeNullOrEmpty();
        mime.TextBody.Should().Be("text");
        mime.HtmlBody.Should().Be("<p>html</p>");
        mime.Attachments.OfType<MimePart>().Should().ContainSingle().Which.FileName.Should().Be("a.txt");
    }

    /// <summary>
    /// Verifies a message without any recipient is rejected.
    /// </summary>
    [Fact]
    public void Build_NoRecipients_Throws()
    {
        var act = () => OutgoingMimeBuilder.Build(TestData.Account("me@example.com"), new OutgoingMessage { Subject = "x" });

        act.Should().Throw<ArgumentException>();
    }
}
