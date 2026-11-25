using System.Diagnostics;
using Swiftlet.Application.Abstractions;

namespace Swiftlet.Infrastructure.OAuth;

/// <summary>
/// Opens web pages with the operating system's default browser.
/// </summary>
internal sealed class ProcessBrowserLauncher : IBrowserLauncher
{
    /// <inheritdoc />
    public Task OpenAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
        {
            throw new ArgumentException("Only http and https addresses can be opened.", nameof(uri));
        }

        // UseShellExecute hands the address to the OS default handler on Windows, macOS and Linux (xdg-open).
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        return Task.CompletedTask;
    }
}
