using System.Text;
using AwesomeAssertions;
using MimeKit;
using Swiftlet.Infrastructure.Mail;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests that mail declared in, or mislabelled as, legacy charsets is decoded without mojibake.
/// </summary>
public class MimeCharsetTests
{
    /// <summary>
    /// Initializes static members of the <see cref="MimeCharsetTests"/> class; the tests encode bytes with legacy code pages.
    /// </summary>
    static MimeCharsetTests()
    {
        MailEncodings.Register();
    }

    /// <summary>
    /// Builds a raw RFC 822 message from header lines and a body that is already encoded to bytes.
    /// </summary>
    /// <param name="headerLines">The header lines without line endings; non-ASCII text must be passed through <paramref name="rawHeaders"/>.</param>
    /// <param name="body">The encoded body.</param>
    /// <param name="rawHeaders">Extra header lines whose bytes are used as given (8-bit headers).</param>
    /// <returns>The parsed message.</returns>
    private static MimeMessage Parse(string[] headerLines, byte[] body, params byte[][] rawHeaders)
    {
        using var stream = new MemoryStream();
        foreach (var line in headerLines)
        {
            stream.Write(Encoding.ASCII.GetBytes($"{line}\r\n"));
        }

        foreach (var raw in rawHeaders)
        {
            stream.Write(raw);
            stream.Write("\r\n"u8);
        }

        stream.Write("\r\n"u8);
        stream.Write(body);
        stream.Position = 0;
        return MimeMessage.Load(stream);
    }

    /// <summary>
    /// Builds the bytes of a 8-bit header line.
    /// </summary>
    /// <param name="name">The header name.</param>
    /// <param name="value">The header value.</param>
    /// <param name="encodingName">The encoding of the value bytes.</param>
    /// <returns>The raw header line.</returns>
    private static byte[] RawHeader(string name, string value, string encodingName)
    {
        return [.. Encoding.ASCII.GetBytes($"{name}: "), .. Encoding.GetEncoding(encodingName).GetBytes(value)];
    }

    /// <summary>
    /// Verifies bodies are decoded correctly whether the charset is right, wrong, generic or missing.
    /// </summary>
    /// <param name="declaredCharset">The charset parameter of the Content-Type, or <see langword="null"/> for none.</param>
    /// <param name="actualEncoding">The encoding the bytes really use.</param>
    /// <param name="text">The text that was encoded.</param>
    [Theory]
    [InlineData("gb2312", "gb2312", "你好，世界，这是一封测试邮件")]
    [InlineData("gbk", "gbk", "你好，世界，这是一封测试邮件")]
    [InlineData("gb18030", "gb18030", "你好，世界，这是一封测试邮件")]
    [InlineData("big5", "big5", "繁體中文測試郵件")]
    [InlineData("iso-2022-jp", "iso-2022-jp", "こんにちは世界")]
    [InlineData("shift_jis", "shift_jis", "こんにちは世界")]
    [InlineData("koi8-r", "koi8-r", "Привет, мир")]
    [InlineData("windows-1251", "windows-1251", "Привет, мир")]
    [InlineData("us-ascii", "gbk", "你好，世界，这是一封测试邮件")]
    [InlineData("iso-8859-1", "utf-8", "你好，世界 résumé")]
    [InlineData("us-ascii", "utf-8", "你好，世界")]
    [InlineData(null, "utf-8", "你好，世界")]
    [InlineData(null, "gb18030", "你好，世界，这是一封测试邮件")]
    [InlineData("gb2312", "utf-8", "你好，世界")]
    [InlineData("x-unknown-charset", "utf-8", "你好，世界")]
    public void Map_TextBody_DecodesWithoutMojibake(string? declaredCharset, string actualEncoding, string text)
    {
        var contentType = declaredCharset is null ? "text/plain" : $"text/plain; charset={declaredCharset}";
        var message = Parse(
            ["From: a@example.com", $"Content-Type: {contentType}", "Content-Transfer-Encoding: 8bit"],
            Encoding.GetEncoding(actualEncoding).GetBytes(text));

        var mapped = MimeMessageMapper.Map(1, message, false, DateTimeOffset.MinValue);

        mapped.BodyText.Should().Be(text);
    }

    /// <summary>
    /// Verifies a quoted-printable body in a legacy charset is decoded after the transfer encoding is removed.
    /// </summary>
    [Fact]
    public void Map_QuotedPrintableGbkBody_DecodesText()
    {
        const string text = "你好，世界";
        var qp = string.Concat(Encoding.GetEncoding("gbk").GetBytes(text).Select(b => $"={b:X2}"));
        var message = Parse(
            ["From: a@example.com", "Content-Type: text/plain; charset=us-ascii", "Content-Transfer-Encoding: quoted-printable"],
            Encoding.ASCII.GetBytes(qp));

        var mapped = MimeMessageMapper.Map(1, message, false, DateTimeOffset.MinValue);

        mapped.BodyText.Should().Be(text);
    }

    /// <summary>
    /// Verifies HTML without a MIME charset uses the charset declared by its meta element.
    /// </summary>
    /// <param name="meta">The meta element.</param>
    [Theory]
    [InlineData("<meta charset=\"gbk\">")]
    [InlineData("<meta http-equiv=\"Content-Type\" content=\"text/html; charset=GB2312\">")]
    public void Map_HtmlWithMetaCharset_DecodesWithDeclaredCharset(string meta)
    {
        const string text = "你好，世界";
        var html = $"<html><head>{meta}</head><body><p>{text}</p></body></html>";
        var message = Parse(
            ["From: a@example.com", "Content-Type: text/html", "Content-Transfer-Encoding: 8bit"],
            Encoding.GetEncoding("gbk").GetBytes(html));

        var mapped = MimeMessageMapper.Map(1, message, false, DateTimeOffset.MinValue);

        mapped.BodyHtml.Should().Contain(text);
        mapped.BodyText.Should().Contain(text);
    }

    /// <summary>
    /// Verifies a multipart/alternative message decodes both parts independently.
    /// </summary>
    [Fact]
    public void Map_AlternativeWithDifferentCharsets_DecodesEachPart()
    {
        const string text = "你好，世界";
        var boundary = "b1";
        using var body = new MemoryStream();
        body.Write(Encoding.ASCII.GetBytes($"--{boundary}\r\nContent-Type: text/plain; charset=gbk\r\nContent-Transfer-Encoding: 8bit\r\n\r\n"));
        body.Write(Encoding.GetEncoding("gbk").GetBytes(text));
        body.Write(Encoding.ASCII.GetBytes($"\r\n--{boundary}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Transfer-Encoding: 8bit\r\n\r\n"));
        body.Write(Encoding.UTF8.GetBytes($"<p>{text}</p>"));
        body.Write(Encoding.ASCII.GetBytes($"\r\n--{boundary}--\r\n"));
        var message = Parse(
            ["From: a@example.com", $"Content-Type: multipart/alternative; boundary={boundary}"],
            body.ToArray());

        var mapped = MimeMessageMapper.Map(1, message, false, DateTimeOffset.MinValue);

        mapped.BodyText.Should().Be(text);
        mapped.BodyHtml.Should().Be($"<p>{text}</p>");
    }

    /// <summary>
    /// Verifies raw 8-bit subjects and sender names are decoded by guessing the charset (Big5 cannot be told apart from
    /// GB18030 without a declaration, so only GBK and UTF-8 are guessed).
    /// </summary>
    /// <param name="encodingName">The encoding of the raw header bytes.</param>
    /// <param name="subject">The subject.</param>
    /// <param name="sender">The sender display name.</param>
    [Theory]
    [InlineData("gbk", "会议通知", "张三")]
    [InlineData("utf-8", "会议通知", "张三")]
    public void Map_Raw8BitHeaders_DecodesSubjectAndSender(string encodingName, string subject, string sender)
    {
        var message = Parse(
            ["Content-Type: text/plain; charset=utf-8"],
            "body"u8.ToArray(),
            RawHeader("Subject", subject, encodingName),
            RawHeader("From", $"{sender} <sender@example.com>", encodingName));

        var mapped = MimeMessageMapper.Map(1, message, false, DateTimeOffset.MinValue);

        mapped.Subject.Should().Be(subject);
        mapped.FromName.Should().Be(sender);
        mapped.FromAddress.Should().Be("sender@example.com");
    }

    /// <summary>
    /// Verifies RFC 2047 encoded words in legacy charsets still decode (the common, well-formed case).
    /// </summary>
    [Fact]
    public void Map_Rfc2047GbkSubject_DecodesSubject()
    {
        const string subject = "会议通知";
        var encoded = Convert.ToBase64String(Encoding.GetEncoding("gb2312").GetBytes(subject));
        var message = Parse(
            ["Content-Type: text/plain; charset=utf-8", $"Subject: =?gb2312?B?{encoded}?="],
            "body"u8.ToArray());

        var mapped = MimeMessageMapper.Map(1, message, false, DateTimeOffset.MinValue);

        mapped.Subject.Should().Be(subject);
    }

    /// <summary>
    /// Verifies plain English mail is left untouched.
    /// </summary>
    [Fact]
    public void Map_PlainAsciiBody_ReturnsSameText()
    {
        var message = Parse(
            ["From: a@example.com", "Subject: Hello", "Content-Type: text/plain"],
            "Hello, world"u8.ToArray());

        var mapped = MimeMessageMapper.Map(1, message, false, DateTimeOffset.MinValue);

        mapped.BodyText.Should().Be("Hello, world");
        mapped.Subject.Should().Be("Hello");
    }
}
