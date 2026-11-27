using Avalonia;
using Avalonia.Controls;
using Swiftlet.Desktop.ViewModels.Main;
using Swiftlet.Desktop.Views;

namespace Swiftlet.Desktop.Services;

/// <summary>
/// Shows the attachment preview in a borderless window of its own, so the native web view below the attachments cannot cover it.
/// </summary>
public sealed class AvaloniaAttachmentPreviewPresenter : IAttachmentPreviewPresenter
{
    /// <summary>
    /// The shown window, if any.
    /// </summary>
    private AttachmentPreviewWindow? _window;

    /// <summary>
    /// Gets the shown window, if any.
    /// </summary>
    internal AttachmentPreviewWindow? Window => _window;

    /// <inheritdoc />
    public void Show(AttachmentPreviewViewModel viewModel, object anchor)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (anchor is not Control control || TopLevel.GetTopLevel(control) is not { } topLevel)
        {
            return;
        }

        var scaling = topLevel.RenderScaling;
        var origin = control.PointToScreen(new Point(0, 0));
        var size = new PixelSize(
            (int)Math.Ceiling(control.Bounds.Width * scaling), (int)Math.Ceiling(control.Bounds.Height * scaling));
        var anchorRect = new PixelRect(origin, size);

        if (_window is null)
        {
            var window = new AttachmentPreviewWindow { DataContext = viewModel, AnchorRect = anchorRect };
            window.Closed += (_, _) =>
            {
                if (ReferenceEquals(_window, window))
                {
                    _window = null;
                }
            };
            _window = window;
            window.Show();
        }
        else
        {
            _window.AnchorRect = anchorRect;
            _window.DataContext = viewModel;
            _window.Reposition();
        }
    }

    /// <inheritdoc />
    public void Hide()
    {
        var window = _window;
        _window = null;
        window?.Close();
    }
}
