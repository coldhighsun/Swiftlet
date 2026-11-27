namespace Swiftlet.Desktop.ViewModels.Compose;

/// <summary>
/// A message body split into what the user wrote and the quoted or forwarded mail below it, with <c>\n</c> line ends.
/// </summary>
/// <param name="Own">The user's text, without surrounding empty lines.</param>
/// <param name="Quote">The quoted or forwarded part; empty if there is none.</param>
public sealed record ComposeBodyParts(string Own, string Quote)
{
    /// <summary>
    /// Splits a body. The quote is looked for where it was put in; if it was edited, at its first line, and failing that
    /// at the first quoted line (with the attribution line above it). Everything from there on counts as quote, so doubt
    /// leaves text unchanged rather than rewriting other people's words.
    /// </summary>
    /// <param name="body">The body.</param>
    /// <param name="originalQuote">The quote the window was opened with; empty for a new message.</param>
    /// <returns>The parts.</returns>
    public static ComposeBodyParts Split(string body, string originalQuote)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(originalQuote);
        var text = body.ReplaceLineEndings("\n");
        var quote = originalQuote.ReplaceLineEndings("\n");
        if (quote.Length > 0 && text.EndsWith(quote, StringComparison.Ordinal))
        {
            return Create(text, text.Length - quote.Length);
        }

        var lines = text.Split('\n');
        var header = quote.Split('\n')[0];
        var start = header.Length > 0 ? Array.IndexOf(lines, header) : -1;
        if (start < 0)
        {
            start = Array.FindIndex(lines, line => line.StartsWith('>'));
            if (start > 0 && (lines[start - 1].EndsWith(':') || lines[start - 1].EndsWith('：')))
            {
                start--;
            }
        }

        return Create(text, start < 0 ? text.Length : lines.Take(start).Sum(line => line.Length + 1));
    }

    /// <summary>
    /// Puts a text of the user above a quote, separated by an empty line.
    /// </summary>
    /// <param name="own">The user's text.</param>
    /// <param name="quote">The quote; may be empty.</param>
    /// <returns>The body.</returns>
    public static string Join(string own, string quote)
    {
        ArgumentNullException.ThrowIfNull(own);
        ArgumentNullException.ThrowIfNull(quote);
        var text = own.ReplaceLineEndings("\n").Trim('\n');
        return quote.Length == 0 ? text : $"{text}\n\n{quote}";
    }

    /// <summary>
    /// Cuts a body at a position.
    /// </summary>
    /// <param name="text">The body.</param>
    /// <param name="quoteStart">Where the quote starts.</param>
    /// <returns>The parts.</returns>
    private static ComposeBodyParts Create(string text, int quoteStart)
    {
        quoteStart = Math.Min(quoteStart, text.Length);
        return new ComposeBodyParts(text[..quoteStart].Trim('\n'), text[quoteStart..]);
    }
}
