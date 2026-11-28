using Avalonia;
using Avalonia.Controls;
using Swiftlet.Desktop.Controls;

namespace Swiftlet.Desktop.Hosting;

/// <summary>
/// Gives up the web views of a window while it is hidden (for example in the tray), so that their browser processes end
/// and their memory is free. They are created again, and load their document again, when the window is shown.
/// </summary>
internal static class ReleaseWebViewWhenHidden
{
    /// <summary>
    /// Follows the visibility of a window from now on.
    /// </summary>
    /// <param name="window">The window.</param>
    /// <param name="enabled">Whether web views are released; asked every time the window is hidden.</param>
    public static void Attach(Window window, Func<bool> enabled)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(enabled);

        // Applied at once too: a window that starts hidden must not create a web view for a message selected meanwhile.
        Apply(window, enabled);
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.IsVisibleProperty)
            {
                Apply(window, enabled);
            }
        };
    }

    /// <summary>
    /// Releases or restores the web views for the current visibility.
    /// </summary>
    /// <param name="window">The window.</param>
    /// <param name="enabled">Whether web views are released.</param>
    private static void Apply(Window window, Func<bool> enabled)
    {
        HtmlBodyView.SetIsReleased(window, !window.IsVisible && enabled());
    }
}
