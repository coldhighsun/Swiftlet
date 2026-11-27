using Avalonia.Threading;
using Swiftlet.Desktop.ViewModels;
using Swiftlet.Desktop.Views;

namespace Swiftlet.Desktop.Services;

/// <summary>
/// Shows the new mail pop-up in a window of its own, which disappears after a few seconds unless the pointer rests on it.
/// </summary>
public sealed class AvaloniaNewMailPresenter : INewMailPresenter
{
    /// <summary>
    /// How long the pop-up stays without being touched.
    /// </summary>
    private static readonly TimeSpan DisplayTime = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Counts down until the pop-up disappears.
    /// </summary>
    private readonly DispatcherTimer _timer;

    /// <summary>
    /// The shown window, if any.
    /// </summary>
    private NewMailWindow? _window;

    /// <summary>
    /// Initializes a new instance of the <see cref="AvaloniaNewMailPresenter"/> class.
    /// </summary>
    /// <param name="displayTime">How long the pop-up stays; optional.</param>
    public AvaloniaNewMailPresenter(TimeSpan? displayTime = null)
    {
        _timer = new DispatcherTimer { Interval = displayTime ?? DisplayTime };
        _timer.Tick += (_, _) =>
        {
            // An action that is still running keeps the pop-up, so its outcome can be seen; the countdown starts over.
            if (_window?.DataContext is NewMailViewModel { IsBusy: true })
            {
                return;
            }

            _timer.Stop();
            (_window?.DataContext as NewMailViewModel)?.CloseCommand.Execute(null);
        };
    }

    /// <summary>
    /// Gets the shown window, if any.
    /// </summary>
    internal NewMailWindow? Window => _window;

    /// <inheritdoc />
    public void Show(NewMailViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (_window is null)
        {
            var window = new NewMailWindow { DataContext = viewModel };
            window.PointerEntered += (_, _) => _timer.Stop();
            window.PointerExited += (_, _) => Restart(window);
            window.Closed += (_, _) => OnWindowClosed(window);
            _window = window;
            window.Show();
        }
        else
        {
            _window.DataContext = viewModel;
        }

        if (!_window.IsPointerOver)
        {
            Restart(_window);
        }
    }

    /// <inheritdoc />
    public void Close()
    {
        _timer.Stop();
        var window = _window;
        _window = null;
        window?.Close();
    }

    /// <summary>
    /// Starts the countdown from the beginning, unless the window is no longer the shown one.
    /// </summary>
    /// <param name="window">The window.</param>
    private void Restart(NewMailWindow window)
    {
        if (!ReferenceEquals(window, _window))
        {
            return;
        }

        _timer.Stop();
        _timer.Start();
    }

    /// <summary>
    /// Tells the view model when the window was closed by someone else, for example with the keyboard.
    /// </summary>
    /// <param name="window">The closed window.</param>
    private void OnWindowClosed(NewMailWindow window)
    {
        if (!ReferenceEquals(window, _window))
        {
            return;
        }

        _timer.Stop();
        _window = null;
        (window.DataContext as NewMailViewModel)?.CloseCommand.Execute(null);
    }
}
