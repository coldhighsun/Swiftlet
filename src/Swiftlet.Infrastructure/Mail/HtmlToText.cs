using System.Net;
using System.Text.RegularExpressions;

namespace Swiftlet.Infrastructure.Mail;

/// <summary>
/// Converts HTML mail bodies to plain text for previews and full-text indexing.
/// </summary>
internal static partial class HtmlToText
{
    /// <summary>
    /// Matches script and style elements including their content.
    /// </summary>
    /// <returns>The compiled regex.</returns>
    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, matchTimeoutMilliseconds: 2000)]
    private static partial Regex NonContentElements();

    /// <summary>
    /// Matches tags that imply a line break.
    /// </summary>
    /// <returns>The compiled regex.</returns>
    [GeneratedRegex(@"<br\s*/?>|</(p|div|tr|li|h[1-6])\s*>", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex LineBreakTags();

    /// <summary>
    /// Matches any remaining tag.
    /// </summary>
    /// <returns>The compiled regex.</returns>
    [GeneratedRegex(@"<[^>]+>", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex AnyTag();

    /// <summary>
    /// Matches runs of horizontal whitespace.
    /// </summary>
    /// <returns>The compiled regex.</returns>
    [GeneratedRegex(@"[ \t ]+", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex HorizontalSpace();

    /// <summary>
    /// Matches three or more consecutive line breaks.
    /// </summary>
    /// <returns>The compiled regex.</returns>
    [GeneratedRegex(@"(\r?\n\s*){3,}", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex ManyLineBreaks();

    /// <summary>
    /// Converts HTML to plain text.
    /// </summary>
    /// <param name="html">The HTML, or <see langword="null"/>.</param>
    /// <returns>The plain text; empty when <paramref name="html"/> is null or blank.</returns>
    public static string Convert(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var text = NonContentElements().Replace(html, string.Empty);
        text = LineBreakTags().Replace(text, "\n");
        text = AnyTag().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = HorizontalSpace().Replace(text, " ");
        text = ManyLineBreaks().Replace(text, "\n\n");
        return text.Trim();
    }
}
