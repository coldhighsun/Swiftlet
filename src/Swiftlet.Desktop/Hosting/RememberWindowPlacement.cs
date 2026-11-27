using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Swiftlet.Desktop.Settings;

namespace Swiftlet.Desktop.Hosting;

/// <summary>
/// Saves the position, size and maximized state of a window when it closes or is hidden in the tray, and brings them
/// back whenever it is shown.
/// </summary>
public sealed class RememberWindowPlacement
{
    /// <summary>
    /// The height of the strip at the top of a window that must be reachable, in device-independent units.
    /// </summary>
    private const double TitleBarHeight = 32;

    /// <summary>
    /// How much of the width of that strip must be on a screen, in device-independent units.
    /// </summary>
    private const double ReachableWidth = 100;

    /// <summary>
    /// The window.
    /// </summary>
    private readonly Window _window;

    /// <summary>
    /// The settings holding the saved placement.
    /// </summary>
    private readonly IAppSettings _settings;

    /// <summary>
    /// The logger, if any.
    /// </summary>
    private readonly ILogger? _logger;

    /// <summary>
    /// The last placement seen while the window was on screen.
    /// </summary>
    private WindowPlacement? _current;

    /// <summary>
    /// Initializes a new instance of the <see cref="RememberWindowPlacement"/> class.
    /// </summary>
    /// <param name="window">The window.</param>
    /// <param name="settings">The settings.</param>
    /// <param name="logger">The logger, if any.</param>
    private RememberWindowPlacement(Window window, IAppSettings settings, ILogger? logger)
    {
        _window = window;
        _settings = settings;
        _logger = logger;
        _current = settings.MainWindowPlacement;
    }

    /// <summary>
    /// Applies the saved placement to a window that was not shown yet and keeps it up to date. Attach this before
    /// <see cref="CloseToTray"/>, so the window is still visible when its placement is read while closing.
    /// </summary>
    /// <param name="window">The window.</param>
    /// <param name="settings">The settings.</param>
    /// <param name="logger">The logger, if any.</param>
    public static void Attach(Window window, IAppSettings settings, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(settings);
        var tracker = new RememberWindowPlacement(window, settings, logger);
        tracker.ApplyBounds();

        // Opened is raised again whenever a window hidden in the tray is shown.
        window.Opened += (_, _) => tracker.Restore();
        window.PositionChanged += (_, _) => tracker.CaptureLater();
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == TopLevel.ClientSizeProperty || e.Property == Window.WindowStateProperty)
            {
                tracker.CaptureLater();
            }
        };
        window.Closing += (_, _) => tracker.Save();
    }

    /// <summary>
    /// Adjusts a saved placement to the screens present now. A window whose title bar cannot be reached, for example
    /// because its screen was unplugged, is centered on the first screen.
    /// </summary>
    /// <param name="saved">The saved placement.</param>
    /// <param name="screens">The usable area of each screen in pixels with its scaling, the primary screen first.</param>
    /// <param name="minimum">The smallest size of the window.</param>
    /// <returns>The placement to apply.</returns>
    internal static WindowPlacement Fit(
        WindowPlacement saved,
        IReadOnlyList<(PixelRect WorkingArea, double Scaling)> screens,
        Size minimum)
    {
        var placement = saved with
        {
            Width = Math.Max(saved.Width, minimum.Width),
            Height = Math.Max(saved.Height, minimum.Height),
        };
        if (screens.Count == 0 || screens.Any(s => IsReachable(placement, s.WorkingArea, s.Scaling)))
        {
            return placement;
        }

        var (area, scaling) = screens[0];
        var width = Math.Max(Math.Min(placement.Width, area.Width / scaling), minimum.Width);
        var height = Math.Max(Math.Min(placement.Height, area.Height / scaling), minimum.Height);
        return placement with
        {
            X = area.X + Math.Max(0, (area.Width - (int)(width * scaling)) / 2),
            Y = area.Y + Math.Max(0, (area.Height - (int)(height * scaling)) / 2),
            Width = width,
            Height = height,
        };
    }

    /// <summary>
    /// Tells whether enough of the top of a window is on a screen to grab it.
    /// </summary>
    /// <param name="placement">The placement.</param>
    /// <param name="area">The usable area of the screen, in pixels.</param>
    /// <param name="scaling">The pixels per device-independent unit.</param>
    /// <returns><see langword="true"/> if the window can be grabbed there.</returns>
    private static bool IsReachable(WindowPlacement placement, PixelRect area, double scaling)
    {
        var width = Math.Min(placement.X + (int)(placement.Width * scaling), area.Right) - Math.Max(placement.X, area.X);
        var height = Math.Min(placement.Y + (int)(TitleBarHeight * scaling), area.Bottom) - Math.Max(placement.Y, area.Y);
        return width >= ReachableWidth * scaling && height >= TitleBarHeight * scaling / 2;
    }

    /// <summary>
    /// Moves and resizes the window to the remembered placement, leaving the maximized state alone.
    /// </summary>
    private void ApplyBounds()
    {
        if (_current is not { } placement)
        {
            return;
        }

        var screens = _window.Screens.All
            .OrderByDescending(s => s.IsPrimary)
            .Select(s => (s.WorkingArea, s.Scaling))
            .ToList();
        var fitted = Fit(placement, screens, new Size(_window.MinWidth, _window.MinHeight));
        _window.WindowStartupLocation = WindowStartupLocation.Manual;
        _window.Position = new PixelPoint(fitted.X, fitted.Y);
        _window.Width = fitted.Width;
        _window.Height = fitted.Height;
        _current = fitted;
    }

    /// <summary>
    /// Brings the shown window back to the remembered placement.
    /// </summary>
    private void Restore()
    {
        // A minimized window is restored by whoever shows it, and a maximized one already is where it belongs.
        if (_current is not { } placement || _window.WindowState != WindowState.Normal)
        {
            return;
        }

        // The bounds come first, so leaving the maximized state returns to them.
        ApplyBounds();
        if (placement.IsMaximized)
        {
            _window.WindowState = WindowState.Maximized;
        }
    }

    /// <summary>
    /// Reads the placement once the pending changes are through: a window being maximized reports its new position and
    /// size before its new state.
    /// </summary>
    private void CaptureLater()
    {
        Dispatcher.UIThread.Post(Capture);
    }

    /// <summary>
    /// Remembers where the window is, as long as it is on screen.
    /// </summary>
    private void Capture()
    {
        if (!_window.IsVisible || _window.WindowState == WindowState.Minimized)
        {
            return;
        }

        if (_window.WindowState == WindowState.Normal)
        {
            var size = _window.ClientSize;
            _current = new WindowPlacement(_window.Position.X, _window.Position.Y, size.Width, size.Height, false);
        }
        else
        {
            // The bounds of the normal state stay, so only the state changes.
            var normal = _current
                ?? new WindowPlacement(_window.Position.X, _window.Position.Y, _window.Width, _window.Height, false);
            _current = normal with { IsMaximized = true };
        }
    }

    /// <summary>
    /// Saves the remembered placement.
    /// </summary>
    private void Save()
    {
        Capture();
        if (_current is not { HasUsableSize: true } placement)
        {
            return;
        }

        try
        {
            _settings.SetMainWindowPlacement(placement);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Closing the window must not fail; it only opens at the previous place next time.
            _logger?.LogWarning(ex, "Saving the window placement failed");
        }
    }
}
