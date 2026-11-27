using Avalonia.Controls;

namespace Swiftlet.Desktop.Hosting;

/// <summary>
/// Makes the close button of a window hide it, so the application keeps running behind its tray icon.
/// </summary>
public static class CloseToTray
{
    /// <summary>
    /// Decides whether a closing window is hidden instead. Only the user closing the window counts: quitting the
    /// application, ending the system session and closing from code go through.
    /// </summary>
    /// <param name="enabled">Whether the user wants the application to stay in the tray.</param>
    /// <param name="reason">Why the window is closing.</param>
    /// <param name="isProgrammatic">Whether code asked the window to close.</param>
    /// <returns><see langword="true"/> if the window is hidden and stays open.</returns>
    public static bool ShouldHide(bool enabled, WindowCloseReason reason, bool isProgrammatic)
    {
        return enabled && reason == WindowCloseReason.WindowClosing && !isProgrammatic;
    }

    /// <summary>
    /// Applies the rule to a window.
    /// </summary>
    /// <param name="window">The window.</param>
    /// <param name="enabled">Reads the current setting each time the window is closing.</param>
    public static void Attach(Window window, Func<bool> enabled)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(enabled);
        window.Closing += (_, e) =>
        {
            if (ShouldHide(enabled(), e.CloseReason, e.IsProgrammatic))
            {
                e.Cancel = true;
                window.Hide();
            }
        };
    }
}
