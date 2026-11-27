namespace Swiftlet.Desktop.Settings;

/// <summary>
/// Where a window was and how large it was.
/// </summary>
/// <param name="X">The left edge of the window when not maximized, in screen pixels.</param>
/// <param name="Y">The top edge of the window when not maximized, in screen pixels.</param>
/// <param name="Width">The width when not maximized, in device-independent units.</param>
/// <param name="Height">The height when not maximized, in device-independent units.</param>
/// <param name="IsMaximized">Whether the window was maximized.</param>
public sealed record WindowPlacement(int X, int Y, double Width, double Height, bool IsMaximized)
{
    /// <summary>
    /// Gets a value indicating whether the size can be applied to a window.
    /// </summary>
    internal bool HasUsableSize => double.IsFinite(Width) && double.IsFinite(Height) && Width > 0 && Height > 0;
}
