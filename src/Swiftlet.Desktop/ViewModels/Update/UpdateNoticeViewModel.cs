using CommunityToolkit.Mvvm.Input;
using Swiftlet.Desktop.Localization;

namespace Swiftlet.Desktop.ViewModels.Update;

/// <summary>
/// The view model of the pop-up saying that a newer version is available.
/// </summary>
/// <param name="version">The newer version.</param>
/// <param name="localizer">The localizer.</param>
/// <param name="open">Shows the update window.</param>
/// <param name="close">Closes the pop-up.</param>
public sealed partial class UpdateNoticeViewModel(string version, ILocalizer localizer, Action open, Action close)
{
    /// <summary>
    /// Gets the text of the pop-up.
    /// </summary>
    public string Text { get; } = localizer.Format("Update_NoticeText", version);

    /// <summary>
    /// Closes the pop-up and shows the update window.
    /// </summary>
    [RelayCommand]
    private void Open()
    {
        close();
        open();
    }

    /// <summary>
    /// Closes the pop-up.
    /// </summary>
    [RelayCommand]
    private void Close()
    {
        close();
    }
}
