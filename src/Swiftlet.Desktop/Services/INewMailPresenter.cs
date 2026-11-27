using Swiftlet.Desktop.ViewModels;

namespace Swiftlet.Desktop.Services;

/// <summary>
/// Puts the new mail pop-up on screen. Used on the UI thread only.
/// </summary>
public interface INewMailPresenter
{
    /// <summary>
    /// Shows the pop-up, or keeps the one already shown for a while longer. The pop-up runs the view model's close
    /// command when its time is up or the window is closed.
    /// </summary>
    /// <param name="viewModel">The content.</param>
    void Show(NewMailViewModel viewModel);

    /// <summary>
    /// Removes the pop-up; does nothing when none is shown.
    /// </summary>
    void Close();
}
