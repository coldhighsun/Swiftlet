using Microsoft.Extensions.Logging;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Services;
using Swiftlet.Desktop.ViewModels.Update;

namespace Swiftlet.Desktop.Hosting;

/// <summary>
/// Runs the automatic update checks while the application runs and tells the user about a found version: with the
/// update window while the main window is on screen, else with a pop-up. It also quits once the installer runs.
/// </summary>
public sealed class UpdateNotifier : IDisposable
{
    /// <summary>
    /// The update service.
    /// </summary>
    private readonly IUpdateService _updates;

    /// <summary>
    /// The localizer.
    /// </summary>
    private readonly ILocalizer _localizer;

    /// <summary>
    /// The dialogs, which show the update window.
    /// </summary>
    private readonly IDialogService _dialogs;

    /// <summary>
    /// Shows the pop-up.
    /// </summary>
    private readonly IUpdateNoticePresenter _presenter;

    /// <summary>
    /// Tells whether the user can see the main window.
    /// </summary>
    private readonly Func<bool> _isMainWindowShown;

    /// <summary>
    /// Ends the application.
    /// </summary>
    private readonly Action _quit;

    /// <summary>
    /// The logger.
    /// </summary>
    private readonly ILogger? _logger;

    /// <summary>
    /// Ends the automatic checks.
    /// </summary>
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>
    /// Whether the instance was disposed.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="UpdateNotifier"/> class.
    /// </summary>
    /// <param name="updates">The update service.</param>
    /// <param name="localizer">The localizer.</param>
    /// <param name="dialogs">The dialogs.</param>
    /// <param name="presenter">Shows the pop-up.</param>
    /// <param name="isMainWindowShown">Tells whether the user can see the main window.</param>
    /// <param name="quit">Ends the application.</param>
    /// <param name="logger">The logger; optional.</param>
    public UpdateNotifier(
        IUpdateService updates,
        ILocalizer localizer,
        IDialogService dialogs,
        IUpdateNoticePresenter presenter,
        Func<bool> isMainWindowShown,
        Action quit,
        ILogger<UpdateNotifier>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(updates);
        _updates = updates;
        _localizer = localizer;
        _dialogs = dialogs;
        _presenter = presenter;
        _isMainWindowShown = isMainWindowShown;
        _quit = quit;
        _logger = logger;
        updates.Announced += OnAnnounced;
        updates.InstallerStarted += OnInstallerStarted;
    }

    /// <summary>
    /// Starts the automatic checks; call it on the UI thread.
    /// </summary>
    /// <returns>A task that completes when the checks have ended.</returns>
    public async Task RunAsync()
    {
        try
        {
            await _updates.RunAsync(_stopping.Token);
        }
        catch (Exception ex)
        {
            // Nobody awaits this task, and a failing update check must not disturb the application.
            _logger?.LogWarning(ex, "The automatic update checks stopped");
        }
    }

    /// <summary>
    /// Stops the checks and closes the pop-up.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _updates.Announced -= OnAnnounced;
        _updates.InstallerStarted -= OnInstallerStarted;
        _stopping.Cancel();
        _stopping.Dispose();
        _presenter.Close();
    }

    /// <summary>
    /// Tells the user about the version that was found.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private void OnAnnounced(object? sender, EventArgs e)
    {
        if (_updates.Available is not { } available)
        {
            return;
        }

        if (_isMainWindowShown())
        {
            _presenter.Close();
            _ = ShowDialogAsync();
            return;
        }

        _presenter.Show(new UpdateNoticeViewModel(
            available.Version,
            _localizer,
            () => _ = ShowDialogAsync(),
            _presenter.Close));
    }

    /// <summary>
    /// Shows the update window.
    /// </summary>
    /// <returns>A task that completes when the window has closed.</returns>
    private async Task ShowDialogAsync()
    {
        try
        {
            await _dialogs.ShowUpdateAsync();
        }
        catch (Exception ex)
        {
            // Nobody awaits this task; a window that cannot be shown must not end the application.
            _logger?.LogWarning(ex, "The update window could not be shown");
        }
    }

    /// <summary>
    /// Quits, so the installer can replace the files.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private void OnInstallerStarted(object? sender, EventArgs e)
    {
        _quit();
    }
}
