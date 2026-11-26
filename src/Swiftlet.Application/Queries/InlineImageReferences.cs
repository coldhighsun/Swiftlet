using System.Text.RegularExpressions;

namespace Swiftlet.Application.Queries;

/// <summary>
/// Finds and rewrites the <c>cid:</c> references that HTML mail uses to point at images embedded in the message.
/// </summary>
public static partial class InlineImageReferences
{
    /// <summary>
    /// Lists the distinct content identifiers referenced by an HTML body.
    /// </summary>
    /// <param name="html">The HTML body, or <see langword="null"/>.</param>
    /// <returns>The normalized content identifiers; empty when there are none.</returns>
    public static IReadOnlyCollection<string> Find(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return [];
        }

        return [.. CidReference().Matches(html)
            .Select(m => Normalize(m.Groups[1].Value))
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Replaces every <c>cid:</c> reference whose image is known.
    /// </summary>
    /// <param name="html">The HTML body.</param>
    /// <param name="resolve">Maps a normalized content identifier to its replacement URL, or <see langword="null"/> to keep the reference.</param>
    /// <returns>The rewritten HTML.</returns>
    public static string Replace(string html, Func<string, string?> resolve)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(resolve);
        return CidReference().Replace(html, match => resolve(Normalize(match.Groups[1].Value)) ?? match.Value);
    }

    /// <summary>
    /// Normalizes a content identifier for comparison: URL-decoded, without angle brackets and case-folded.
    /// </summary>
    /// <param name="contentId">The raw identifier from a <c>cid:</c> URL or a Content-ID header.</param>
    /// <returns>The normalized identifier.</returns>
    public static string Normalize(string contentId)
    {
        ArgumentNullException.ThrowIfNull(contentId);
        var decoded = Uri.UnescapeDataString(contentId.Trim());
        return decoded.Trim().Trim('<', '>').Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Matches the identifier of a <c>cid:</c> URL up to the closing quote, parenthesis, whitespace or tag end.
    /// </summary>
    /// <returns>The compiled regex.</returns>
    [GeneratedRegex(@"\bcid:([^""'\s)>]+)", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 2000)]
    private static partial Regex CidReference();
}
