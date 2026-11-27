using Swiftlet.Desktop.ViewModels.Update;

namespace Swiftlet.Desktop.Services;

/// <summary>
/// Puts the "new version" pop-up on the screen.
/// </summary>
public interface IUpdateNoticePresenter
{
    /// <summary>
    /// Shows the pop-up, replacing an open one.
    /// </summary>
    /// <param name="viewModel">The content.</param>
    void Show(UpdateNoticeViewModel viewModel);

    /// <summary>
    /// Closes the pop-up if one is open.
    /// </summary>
    void Close();
}
