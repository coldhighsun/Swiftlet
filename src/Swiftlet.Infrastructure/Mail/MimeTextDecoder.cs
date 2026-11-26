using System.Text;
using System.Text.RegularExpressions;
using MimeKit;

namespace Swiftlet.Infrastructure.Mail;

/// <summary>
/// Decodes the text of a MIME part, and of raw 8-bit headers, into a string without trusting the declared charset blindly:
/// mail clients regularly mislabel GBK or UTF-8 text as us-ascii / iso-8859-1, or declare nothing at all.
/// </summary>
internal static partial class MimeTextDecoder
{
    /// <summary>
    /// The number of leading bytes of an HTML part searched for a <c>meta</c> charset declaration.
    /// </summary>
    private const int MetaScanLength = 4096;

    /// <summary>
    /// The minimum number of non-ASCII characters before valid UTF-8 overrides a declared legacy charset.
    /// </summary>
    private const int MinNonAsciiForUtf8Override = 2;

    /// <summary>
    /// The first byte of an ISO-2022-JP escape sequence.
    /// </summary>
    private const byte Escape = 0x1B;

    /// <summary>
    /// The charset names that are declared far more often than they are true, so they are verified instead of trusted.
    /// </summary>
    private static readonly HashSet<string> UnreliableCharsets = new(StringComparer.OrdinalIgnoreCase)
    {
        "us-ascii",
        "ascii",
        "iso-8859-1",
        "latin1",
        "windows-1252",
        "cp1252",
    };

    /// <summary>
    /// The encodings tried, in order, when the declared charset is missing or unreliable. The order favours UTF-8
    /// (which almost never decodes by accident), then the Chinese encodings.
    /// </summary>
    private static readonly string[] GuessOrder = ["utf-8", "gb18030", "big5"];

    /// <summary>
    /// Initializes static members of the <see cref="MimeTextDecoder"/> class.
    /// </summary>
    static MimeTextDecoder()
    {
        MailEncodings.Register();
    }

    /// <summary>
    /// Decodes a text part.
    /// </summary>
    /// <param name="part">The part, or <see langword="null"/>.</param>
    /// <returns>The text, or <see langword="null"/> when there is no part.</returns>
    public static string? Decode(TextPart? part)
    {
        if (part is null)
        {
            return null;
        }

        using var buffer = new MemoryStream();
        part.Content?.DecodeTo(buffer);
        var bytes = buffer.ToArray();
        var declared = FindEncoding(part.ContentType.Charset);
        if (declared is null && part.IsHtml)
        {
            declared = FindEncoding(FindMetaCharset(bytes));
        }

        return Decode(bytes, declared);
    }

    /// <summary>
    /// Decodes bytes that carry no reliable charset information, such as an 8-bit header.
    /// </summary>
    /// <param name="bytes">The raw bytes.</param>
    /// <returns>The decoded text.</returns>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        return Decode(bytes, null);
    }

    /// <summary>
    /// Decodes bytes using the declared encoding when it holds up, otherwise by guessing.
    /// </summary>
    /// <param name="bytes">The raw bytes.</param>
    /// <param name="declared">The declared encoding, or <see langword="null"/> when none is known.</param>
    /// <returns>The decoded text.</returns>
    private static string Decode(ReadOnlySpan<byte> bytes, Encoding? declared)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        if (declared is not null && !UnreliableCharsets.Contains(declared.WebName))
        {
            // Text that is valid UTF-8 and has several non-ASCII characters is practically never a legacy encoding that
            // happens to look like UTF-8, so a UTF-8 reading wins over a wrong declaration such as gb2312.
            if (declared.CodePage != Encoding.UTF8.CodePage
                && TryDecodeStrict(bytes, "utf-8") is { } utf8
                && utf8.Count(c => c > 0x7F) >= MinNonAsciiForUtf8Override)
            {
                return utf8;
            }

            var text = declared.GetString(bytes);
            if (!text.Contains((char)0xFFFD))
            {
                return text;
            }

            // The declared charset cannot represent the bytes; a mislabelled UTF-8 body is the usual cause.
            return TryDecodeStrict(bytes, "utf-8") ?? text;
        }

        if (bytes.IndexOfAnyInRange((byte)0x80, (byte)0xFF) < 0)
        {
            return DecodePlainAscii(bytes);
        }

        foreach (var name in GuessOrder)
        {
            var text = TryDecodeStrict(bytes, name);
            if (text is not null)
            {
                return text;
            }
        }

        return (declared ?? Encoding.GetEncoding("windows-1252")).GetString(bytes);
    }

    /// <summary>
    /// Decodes 7-bit data, honouring an ISO-2022-JP escape sequence that is not declared.
    /// </summary>
    /// <param name="bytes">The 7-bit bytes.</param>
    /// <returns>The decoded text.</returns>
    private static string DecodePlainAscii(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Contains(Escape))
        {
            var japanese = TryDecodeStrict(bytes, "iso-2022-jp");
            if (japanese is not null)
            {
                return japanese;
            }
        }

        return Encoding.ASCII.GetString(bytes);
    }

    /// <summary>
    /// Decodes with the named encoding and gives up on the first invalid byte sequence.
    /// </summary>
    /// <param name="bytes">The raw bytes.</param>
    /// <param name="encodingName">The encoding name.</param>
    /// <returns>The text, or <see langword="null"/> when the bytes are not valid in that encoding.</returns>
    private static string? TryDecodeStrict(ReadOnlySpan<byte> bytes, string encodingName)
    {
        var encoding = Encoding.GetEncoding(encodingName, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        try
        {
            return encoding.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Expected while guessing: the bytes simply are not valid in this encoding, so the next candidate is tried.
            return null;
        }
    }

    /// <summary>
    /// Looks up an encoding by charset name.
    /// </summary>
    /// <param name="charset">The charset name, possibly <see langword="null"/>.</param>
    /// <returns>The encoding, or <see langword="null"/> when the name is empty or unknown.</returns>
    private static Encoding? FindEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
        {
            return null;
        }

        try
        {
            return Encoding.GetEncoding(charset.Trim());
        }
        catch (ArgumentException)
        {
            // An unknown charset name is common in the wild; the caller then guesses from the bytes instead.
            return null;
        }
    }

    /// <summary>
    /// Reads the charset declared by a <c>meta</c> element near the start of an HTML document.
    /// </summary>
    /// <param name="bytes">The raw document bytes.</param>
    /// <returns>The charset name, or <see langword="null"/> when none is declared.</returns>
    private static string? FindMetaCharset(byte[] bytes)
    {
        var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, MetaScanLength));
        var match = MetaCharset().Match(head);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Matches <c>&lt;meta charset="..."&gt;</c> and <c>&lt;meta http-equiv="Content-Type" content="...; charset=..."&gt;</c>.
    /// </summary>
    /// <returns>The compiled regex.</returns>
    [GeneratedRegex(@"<meta\b[^>]*?charset\s*=\s*[""']?\s*([A-Za-z0-9_\-:.]+)", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex MetaCharset();
}
