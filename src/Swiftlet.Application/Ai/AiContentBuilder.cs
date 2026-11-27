using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Application.Ai;

/// <summary>
/// The mail text prepared for a request.
/// </summary>
/// <param name="Text">The message between its delimiters.</param>
/// <param name="Tag">The name of the delimiting tag.</param>
/// <param name="IsTruncated">Whether the body was cut at the input limit.</param>
public sealed record AiMailContent(string Text, string Tag, bool IsTruncated);

/// <summary>
/// A text between delimiters.
/// </summary>
/// <param name="Text">The text with its delimiters.</param>
/// <param name="Tag">The name of the delimiting tag.</param>
public sealed record AiDelimitedText(string Text, string Tag);

/// <summary>
/// Puts together what is sent: for a message its subject, sender, date and plain-text body, nothing else.
/// </summary>
public static partial class AiContentBuilder
{
    /// <summary>
    /// The largest number of characters of the subject and of the sender that are sent.
    /// </summary>
    private const int MaxHeaderLength = 500;

    /// <summary>
    /// Puts a text between delimiters with a random name, so the text cannot close them.
    /// </summary>
    /// <param name="kind">What the text is, the start of the tag name.</param>
    /// <param name="text">The text.</param>
    /// <returns>The delimited text.</returns>
    public static AiDelimitedText Wrap(string kind, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tag = $"{kind}-{Guid.NewGuid():N}";
        return new AiDelimitedText($"<{tag}>\n{text.ReplaceLineEndings("\n")}\n</{tag}>", tag);
    }

    /// <summary>
    /// Prepares a message. The delimiting tag is random, so mail text cannot close it and pose as instructions.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <param name="maxBodyCharacters">The input limit for the body.</param>
    /// <returns>The content.</returns>
    public static AiMailContent Build(MailMessage message, int maxBodyCharacters)
    {
        ArgumentNullException.ThrowIfNull(message);
        var body = BlankLines().Replace(message.BodyText.ReplaceLineEndings("\n"), "\n\n").Trim();
        var truncated = body.Length > maxBodyCharacters;
        if (truncated)
        {
            body = Cut(body, maxBodyCharacters);
        }

        var tag = $"email-{Guid.NewGuid():N}";
        var text = new StringBuilder()
            .Append('<').Append(tag).Append(">\n")
            .Append("Subject: ").Append(Header(message.Subject)).Append('\n')
            .Append("From: ").Append(Header($"{message.FromName} <{message.FromAddress}>".Trim())).Append('\n')
            .Append("Date: ").Append(message.SentAt.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture))
            .Append("\n\n")
            .Append(body).Append('\n')
            .Append("</").Append(tag).Append('>')
            .ToString();
        return new AiMailContent(text, tag, truncated);
    }

    /// <summary>
    /// Makes a header value a single line of limited length.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <returns>The line.</returns>
    private static string Header(string value)
    {
        var line = value.ReplaceLineEndings(" ");
        return line.Length > MaxHeaderLength ? Cut(line, MaxHeaderLength) : line;
    }

    /// <summary>
    /// Cuts a text without splitting a surrogate pair.
    /// </summary>
    /// <param name="text">The text, longer than the limit.</param>
    /// <param name="length">The limit.</param>
    /// <returns>The beginning of the text.</returns>
    private static string Cut(string text, int length)
    {
        return text[..(char.IsHighSurrogate(text[length - 1]) ? length - 1 : length)];
    }

    /// <summary>
    /// Matches runs of empty lines, which converted HTML mail often has.
    /// </summary>
    /// <returns>The expression.</returns>
    [GeneratedRegex(@"\n(?:[ \t ]*\n){2,}", RegexOptions.None, 1000)]
    private static partial Regex BlankLines();
}
