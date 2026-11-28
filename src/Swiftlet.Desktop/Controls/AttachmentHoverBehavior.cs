using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Swiftlet.Application.Abstractions;
using Swiftlet.Desktop.Hosting;
using Swiftlet.Desktop.Services;
using Swiftlet.Desktop.ViewModels.Main;

namespace Swiftlet.Desktop.Controls;

/// <summary>
/// Gives a window that shows messages the attachment preview under a resting pointer.
/// </summary>
public static class AttachmentHoverBehavior
{
    /// <summary>
    /// Follows the pointer over a window and previews the attachment it rests on.
    /// </summary>
    /// <param name="window">The window.</param>
    /// <param name="main">Gets the view model that loads the previews, if there is one.</param>
    /// <returns>The controller, so the window can hide the preview when it closes.</returns>
    public static AttachmentHoverController Attach(Window window, Func<MainWindowViewModel?> main)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(main);
        var hover = new AttachmentHoverController(
            (attachment, cancellationToken) => main() is { } viewModel
                ? viewModel.LoadAttachmentPreviewAsync(attachment, cancellationToken)
                : Task.FromResult<AttachmentPreview?>(null),
            attachment => main()!.CreateAttachmentPreview(attachment),
            new AvaloniaAttachmentPreviewPresenter());

        // The attachment buttons live in a data template, so the pointer is tracked on the window and the button under it
        // is looked up. Only the button that opens an attachment previews it; the download button has its own tooltip.
        window.AddHandler(
            InputElement.PointerMovedEvent,
            (_, e) =>
            {
                var button = (e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true);
                if (button?.DataContext is AttachmentItemViewModel attachment && button.Classes.Contains("attachment"))
                {
                    hover.Enter(attachment, button);
                }
                else
                {
                    hover.Leave();
                }
            },
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        window.AddHandler(InputElement.PointerPressedEvent, (_, _) => hover.Leave(), RoutingStrategies.Tunnel, handledEventsToo: true);

        // Scrolling moves the attachments under a resting pointer without a pointer movement.
        window.AddHandler(InputElement.PointerWheelChangedEvent, (_, _) => hover.Leave(), RoutingStrategies.Tunnel, handledEventsToo: true);
        window.PointerExited += (_, _) => hover.Leave();
        window.Deactivated += (_, _) => hover.Leave();
        return hover;
    }
}
