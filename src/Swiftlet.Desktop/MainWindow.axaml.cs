using Avalonia.Controls;
using Avalonia.Threading;
using Swiftlet.Desktop.ViewModels;

namespace Swiftlet.Desktop;

/// <summary>
/// The main application window.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// How often the view reloads the cache, so messages synchronized in the background appear.
    /// </summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The timer that periodically refreshes the view.
    /// </summary>
    private DispatcherTimer? _refreshTimer;

    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindow"/> class.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpenedAsync;
        Closed += OnClosed;
    }

    /// <summary>
    /// Loads the data once the window is shown and starts the periodic refresh.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private async void OnOpenedAsync(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        _refreshTimer = new DispatcherTimer(RefreshInterval, DispatcherPriority.Background, async (_, _) => await viewModel.RefreshAsync());
        _refreshTimer.Start();
        await viewModel.InitializeAsync();
    }

    /// <summary>
    /// Stops the periodic refresh and the sync-triggered refreshes.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private void OnClosed(object? sender, EventArgs e)
    {
        _refreshTimer?.Stop();
        (DataContext as MainWindowViewModel)?.Dispose();
    }
}
