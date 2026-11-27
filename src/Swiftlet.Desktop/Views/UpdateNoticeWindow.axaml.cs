using Avalonia.Controls;

namespace Swiftlet.Desktop.Views;

/// <summary>
/// The pop-up saying that a newer version is available, in the bottom right corner of the primary screen.
/// </summary>
public sealed partial class UpdateNoticeWindow : Window
{
    /// <summary>
    /// Initializes a new instance of the <see cref="UpdateNoticeWindow"/> class.
    /// </summary>
    public UpdateNoticeWindow()
    {
        InitializeComponent();
        Opened += (_, _) => MoveToCorner();
        SizeChanged += (_, _) => MoveToCorner();
    }

    /// <summary>
    /// Places the window; some window systems (Wayland) ignore the request.
    /// </summary>
    private void MoveToCorner()
    {
        if ((Screens.Primary ?? (Screens.All.Count > 0 ? Screens.All[0] : null)) is { } screen)
        {
            Position = NewMailWindow.CornerPosition(screen.WorkingArea, Bounds.Size, screen.Scaling);
        }
    }
}
