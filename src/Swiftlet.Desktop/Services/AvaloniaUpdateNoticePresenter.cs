using Swiftlet.Desktop.Controls;
using Swiftlet.Desktop.ViewModels.Update;
using Swiftlet.Desktop.Views;

namespace Swiftlet.Desktop.Services;

/// <summary>
/// Shows the "new version" pop-up as a window. It stays until the user closes or clicks it, because a version is
/// announced only once.
/// </summary>
public sealed class AvaloniaUpdateNoticePresenter : IUpdateNoticePresenter
{
    /// <summary>
    /// The open pop-up, if any.
    /// </summary>
    private UpdateNoticeWindow? _window;

    /// <summary>
    /// Gets the open pop-up, for tests.
    /// </summary>
    internal UpdateNoticeWindow? Window => _window;

    /// <inheritdoc />
    public void Show(UpdateNoticeViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (_window is not null)
        {
            _window.DataContext = viewModel;
            return;
        }

        var window = new UpdateNoticeWindow { DataContext = viewModel };
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(window, _window))
            {
                _window = null;
            }
        };
        _window = window;
        window.Show();
    }

    /// <inheritdoc />
    public void Close()
    {
        var window = _window;
        _window = null;
        if (window is not null)
        {
            _ = Entrance.CloseWithFadeAsync(window, window.Card);
        }
    }
}
