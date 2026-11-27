using Avalonia;
using Avalonia.Controls;

namespace Swiftlet.Desktop.Views;

/// <summary>
/// The pop-up announcing new mail, in the bottom right corner of the primary screen.
/// </summary>
public sealed partial class NewMailWindow : Window
{
    /// <summary>
    /// The distance to the edges of the screen, in device-independent units.
    /// </summary>
    private const double EdgeMargin = 16;

    /// <summary>
    /// Initializes a new instance of the <see cref="NewMailWindow"/> class.
    /// </summary>
    public NewMailWindow()
    {
        InitializeComponent();
        Opened += (_, _) => MoveToCorner();

        // The height follows the content, so the window moves when a longer subject arrives.
        SizeChanged += (_, _) => MoveToCorner();
    }

    /// <summary>
    /// Computes where a window sits in the bottom right corner of a screen.
    /// </summary>
    /// <param name="workingArea">The usable part of the screen, in pixels.</param>
    /// <param name="size">The window size, in device-independent units.</param>
    /// <param name="scaling">The pixels per device-independent unit.</param>
    /// <returns>The position of the top left corner, in pixels.</returns>
    internal static PixelPoint CornerPosition(PixelRect workingArea, Size size, double scaling)
    {
        var width = (int)Math.Ceiling((size.Width + EdgeMargin) * scaling);
        var height = (int)Math.Ceiling((size.Height + EdgeMargin) * scaling);
        return new PixelPoint(workingArea.Right - width, workingArea.Bottom - height);
    }

    /// <summary>
    /// Places the window; some window systems (Wayland) ignore the request.
    /// </summary>
    private void MoveToCorner()
    {
        if ((Screens.Primary ?? (Screens.All.Count > 0 ? Screens.All[0] : null)) is { } screen)
        {
            Position = CornerPosition(screen.WorkingArea, Bounds.Size, screen.Scaling);
        }
    }
}
