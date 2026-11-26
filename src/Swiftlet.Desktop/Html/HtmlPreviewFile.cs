namespace Swiftlet.Desktop.Html;

/// <summary>
/// Prepares HTML mail bodies for viewing in an external browser.
/// </summary>
public static class HtmlPreviewFile
{
    /// <summary>
    /// A Content-Security-Policy that blocks scripts, remote images, frames and network requests.
    /// </summary>
    private const string Policy = "default-src 'none'; img-src data:; style-src 'unsafe-inline'";

    /// <summary>
    /// Wraps an HTML body in a document that carries a restrictive Content-Security-Policy.
    /// </summary>
    /// <param name="html">The HTML body of the message.</param>
    /// <returns>A complete HTML document.</returns>
    public static string Build(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        return $"<!DOCTYPE html><html><head><meta charset=\"utf-8\">"
            + $"<meta http-equiv=\"Content-Security-Policy\" content=\"{Policy}\"></head><body>{html}</body></html>";
    }
}
