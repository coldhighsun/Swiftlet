using Avalonia;
using Avalonia.Controls;

namespace Swiftlet.Desktop.Views;

/// <summary>
/// The window showing a quick preview of the attachment under the pointer.
/// </summary>
public sealed partial class AttachmentPreviewWindow : Window
{
    /// <summary>
    /// The gap between the attachment and the preview, in device-independent units.
    /// </summary>
    private const double Gap = 6;

    /// <summary>
    /// Initializes a new instance of the <see cref="AttachmentPreviewWindow"/> class.
    /// </summary>
    public AttachmentPreviewWindow()
    {
        InitializeComponent();
        Opened += (_, _) => Reposition();

        // The height follows the content, so the window moves when the preview arrives.
        SizeChanged += (_, _) => Reposition();
    }

    /// <summary>
    /// Gets or sets the screen rectangle of the attachment the preview belongs to, in pixels.
    /// </summary>
    public PixelRect AnchorRect { get; set; }

    /// <summary>
    /// Computes where the preview sits: below the attachment, above it when there is no room below, and inside the screen.
    /// </summary>
    /// <param name="workingArea">The usable part of the screen, in pixels.</param>
    /// <param name="anchor">The attachment, in pixels.</param>
    /// <param name="size">The window size, in pixels.</param>
    /// <param name="gap">The distance to the attachment, in pixels.</param>
    /// <returns>The position of the top left corner, in pixels.</returns>
    internal static PixelPoint Place(PixelRect workingArea, PixelRect anchor, PixelSize size, int gap)
    {
        var x = Math.Max(workingArea.X, Math.Min(anchor.X, workingArea.Right - size.Width));
        var y = anchor.Bottom + gap;
        if (y + size.Height > workingArea.Bottom)
        {
            y = anchor.Y - gap - size.Height;
        }

        y = Math.Max(workingArea.Y, Math.Min(y, workingArea.Bottom - size.Height));
        return new PixelPoint(x, y);
    }

    /// <summary>
    /// Places the window beside its attachment; some window systems (Wayland) ignore the request.
    /// </summary>
    public void Reposition()
    {
        var screen = Screens.ScreenFromPoint(AnchorRect.TopLeft) ?? Screens.Primary
            ?? (Screens.All.Count > 0 ? Screens.All[0] : null);
        if (screen is null || Bounds.Width <= 0)
        {
            return;
        }

        var scaling = screen.Scaling;
        var size = new PixelSize((int)Math.Ceiling(Bounds.Width * scaling), (int)Math.Ceiling(Bounds.Height * scaling));
        Position = Place(screen.WorkingArea, AnchorRect, size, (int)Math.Ceiling(Gap * scaling));
    }
}
