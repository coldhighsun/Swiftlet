using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Queries;

namespace Swiftlet.Desktop.Html;

/// <summary>
/// Prepares HTML mail bodies for the embedded web view and decides which links may leave it.
/// </summary>
public static partial class HtmlMailDocument
{
    /// <summary>
    /// A Content-Security-Policy that blocks scripts, remote images, frames and network requests.
    /// </summary>
    private const string Policy = "default-src 'none'; img-src data:; style-src 'unsafe-inline'";

    /// <summary>
    /// The same policy with remote images allowed; scripts, frames and every other request stay blocked.
    /// </summary>
    private const string PolicyWithRemoteImages = "default-src 'none'; img-src data: https: http:; style-src 'unsafe-inline'";

    /// <summary>
    /// The base style: a white page (mail is written for a light background), wrapping long words and scaled images.
    /// </summary>
    private const string BaseStyle =
        "html{color-scheme:light}body{margin:0;padding:8px;background:#fff;color:#000;"
        + "font-family:'Segoe UI',system-ui,-apple-system,sans-serif;font-size:14px;overflow-wrap:anywhere}"
        + "img{max-width:100%;height:auto}";

    /// <summary>
    /// Wraps an HTML body in a complete document with the restrictive policy and the base style.
    /// </summary>
    /// <param name="html">The HTML body of the message.</param>
    /// <param name="allowRemoteImages">Whether images may be loaded from the web (this tells the sender the mail was opened).</param>
    /// <param name="inlineImages">The images embedded in the message, keyed by normalized content identifier, or <see langword="null"/>.</param>
    /// <returns>A complete HTML document.</returns>
    public static string Build(
        string html,
        bool allowRemoteImages = false,
        IReadOnlyDictionary<string, InlineImage>? inlineImages = null)
    {
        ArgumentNullException.ThrowIfNull(html);
        var body = inlineImages is { Count: > 0 } ? ReplaceInlineImages(html, inlineImages) : html;
        return "<!DOCTYPE html><html><head><meta charset=\"utf-8\">"
            + $"<meta http-equiv=\"Content-Security-Policy\" content=\"{(allowRemoteImages ? PolicyWithRemoteImages : Policy)}\">"
            + "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
            + $"<style>{BaseStyle}</style></head><body>{body}</body></html>";
    }

    /// <summary>
    /// Determines whether an HTML body loads images from the web (images, <c>background</c> attributes or CSS <c>url()</c>).
    /// </summary>
    /// <param name="html">The HTML body, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if remote images are referenced.</returns>
    public static bool HasRemoteImages(string? html)
    {
        return !string.IsNullOrEmpty(html) && RemoteImageReference().IsMatch(html);
    }

    /// <summary>
    /// Determines whether a link clicked in a mail may be handed to the system (web and mail links only).
    /// </summary>
    /// <param name="uri">The target of the link.</param>
    /// <returns><see langword="true"/> for http, https and mailto links.</returns>
    public static bool IsExternalLink([NotNullWhen(true)] Uri? uri)
    {
        return uri is { IsAbsoluteUri: true }
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeMailto);
    }

    /// <summary>
    /// Replaces <c>cid:</c> references with data URIs of the downloaded images.
    /// </summary>
    /// <param name="html">The HTML body.</param>
    /// <param name="images">The downloaded images.</param>
    /// <returns>The rewritten HTML; references without a downloaded image are left unchanged.</returns>
    private static string ReplaceInlineImages(string html, IReadOnlyDictionary<string, InlineImage> images)
    {
        return InlineImageReferences.Replace(
            html,
            id => images.TryGetValue(id, out var image)
                ? $"data:{image.ContentType};base64,{Convert.ToBase64String(image.Content.Span)}"
                : null);
    }

    /// <summary>
    /// Matches an <c>img</c> source, a <c>background</c> attribute or a CSS <c>url()</c> that points at http or https.
    /// </summary>
    /// <returns>The compiled regex.</returns>
    [GeneratedRegex(
        @"<img\b[^>]*?\bsrc\s*=\s*[""']?\s*https?://|\bbackground\s*=\s*[""']?\s*https?://|url\(\s*[""']?\s*https?://",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 2000)]
    private static partial Regex RemoteImageReference();
}
