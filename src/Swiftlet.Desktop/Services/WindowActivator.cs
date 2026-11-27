using Avalonia.Controls;

namespace Swiftlet.Desktop.Services;

/// <summary>
/// Brings a window back in front of the user, whether it is hidden, minimized or covered by other windows.
/// </summary>
public sealed class WindowActivator
{
    /// <summary>
    /// The window.
    /// </summary>
    private readonly Window _window;

    /// <summary>
    /// The last state in which the window was not minimized.
    /// </summary>
    private WindowState _restoreState;

    /// <summary>
    /// Whether the window was closed and cannot be shown again.
    /// </summary>
    private bool _closed;

    /// <summary>
    /// Initializes a new instance of the <see cref="WindowActivator"/> class.
    /// </summary>
    /// <param name="window">The window.</param>
    public WindowActivator(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
        _restoreState = window.WindowState == WindowState.Minimized ? WindowState.Normal : window.WindowState;
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty && window.WindowState != WindowState.Minimized)
            {
                _restoreState = window.WindowState;
            }
        };
        window.Closed += (_, _) => _closed = true;
    }

    /// <summary>
    /// Shows the window, restores it when it is minimized and activates it; does nothing once it is closed.
    /// </summary>
    public void BringToFront()
    {
        if (_closed)
        {
            return;
        }

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = _restoreState;
        }

        _window.Activate();
    }
}
