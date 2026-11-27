using Swiftlet.Desktop.ViewModels.Main;

namespace Swiftlet.Desktop.Services;

/// <summary>
/// Shows the attachment preview next to the attachment the pointer rests on.
/// </summary>
public interface IAttachmentPreviewPresenter
{
    /// <summary>
    /// Shows the preview, or replaces the shown one.
    /// </summary>
    /// <param name="viewModel">What to show.</param>
    /// <param name="anchor">The control of the attachment, which the preview is placed beside.</param>
    void Show(AttachmentPreviewViewModel viewModel, object anchor);

    /// <summary>
    /// Hides the preview, if one is shown.
    /// </summary>
    void Hide();
}
