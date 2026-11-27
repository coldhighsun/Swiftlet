using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Swiftlet.Application.Sync;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Services;
using Swiftlet.Desktop.Settings;
using Swiftlet.Desktop.ViewModels;

namespace Swiftlet.Desktop.Hosting;

/// <summary>
/// Announces newly arrived mail with one pop-up, which collects further arrivals while it is shown.
/// </summary>
public sealed class NewMailNotifier : IDisposable
{
    /// <summary>
    /// The sync notifications.
    /// </summary>
    private readonly ISyncEvents _events;

    /// <summary>
    /// The settings, which can switch the pop-up off.
    /// </summary>
    private readonly IAppSettings _settings;

    /// <summary>
    /// The localizer.
    /// </summary>
    private readonly ILocalizer _localizer;

    /// <summary>
    /// Marshals arrivals to the UI thread.
    /// </summary>
    private readonly IUiThread _uiThread;

    /// <summary>
    /// Puts the pop-up on screen.
    /// </summary>
    private readonly INewMailPresenter _presenter;

    /// <summary>
    /// Shows the mail when the pop-up is clicked.
    /// </summary>
    private readonly Action _open;

    /// <summary>
    /// The logger.
    /// </summary>
    private readonly ILogger _logger;

    /// <summary>
    /// The content of the shown pop-up, if any; touched on the UI thread only.
    /// </summary>
    private NewMailViewModel? _current;

    /// <summary>
    /// Whether the notifier was disposed.
    /// </summary>
    private volatile bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="NewMailNotifier"/> class and starts listening.
    /// </summary>
    /// <param name="events">The sync notifications.</param>
    /// <param name="settings">The settings.</param>
    /// <param name="localizer">The localizer.</param>
    /// <param name="uiThread">The UI thread marshaller.</param>
    /// <param name="presenter">Puts the pop-up on screen.</param>
    /// <param name="open">Shows the mail when the pop-up is clicked.</param>
    /// <param name="logger">The logger; optional.</param>
    public NewMailNotifier(
        ISyncEvents events,
        IAppSettings settings,
        ILocalizer localizer,
        IUiThread uiThread,
        INewMailPresenter presenter,
        Action open,
        ILogger<NewMailNotifier>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        _events = events;
        _settings = settings;
        _localizer = localizer;
        _uiThread = uiThread;
        _presenter = presenter;
        _open = open;
        _logger = logger ?? NullLogger<NewMailNotifier>.Instance;
        events.NewMailArrived += OnNewMailArrived;
    }

    /// <summary>
    /// Stops listening and removes the pop-up; call it on the UI thread.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        _events.NewMailArrived -= OnNewMailArrived;
        Dismiss();
    }

    /// <summary>
    /// Hands an arrival to the UI thread; may be called from any thread.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private void OnNewMailArrived(object? sender, NewMailEventArgs e)
    {
        _ = AnnounceAsync(e);
    }

    /// <summary>
    /// Shows an arrival on the UI thread.
    /// </summary>
    /// <param name="arrival">The arrived mail.</param>
    /// <returns>A task that completes when the pop-up shows the mail or the failure is logged.</returns>
    private async Task AnnounceAsync(NewMailEventArgs arrival)
    {
        try
        {
            await _uiThread.InvokeAsync(() =>
            {
                Present(arrival);
                return Task.CompletedTask;
            });
        }
#pragma warning disable CA1031 // Nobody awaits this notification; a window that cannot be shown must not end the application.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "The new mail notification could not be shown");
        }
    }

    /// <summary>
    /// Creates the pop-up or adds to the shown one.
    /// </summary>
    /// <param name="arrival">The arrived mail.</param>
    private void Present(NewMailEventArgs arrival)
    {
        if (_disposed || !_settings.ShowNewMailNotifications)
        {
            return;
        }

        if (_current is null)
        {
            _current = new NewMailViewModel(arrival, _localizer);
            _current.OpenRequested += OnOpenRequested;
            _current.CloseRequested += OnCloseRequested;
        }
        else
        {
            _current.Add(arrival);
        }

        _presenter.Show(_current);
    }

    /// <summary>
    /// Removes the pop-up and shows the mail.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private void OnOpenRequested(object? sender, EventArgs e)
    {
        Dismiss();
        _open();
    }

    /// <summary>
    /// Removes the pop-up.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private void OnCloseRequested(object? sender, EventArgs e)
    {
        Dismiss();
    }

    /// <summary>
    /// Forgets the shown pop-up, so the next arrival starts a new count.
    /// </summary>
    private void Dismiss()
    {
        if (_current is not { } current)
        {
            return;
        }

        _current = null;
        current.OpenRequested -= OnOpenRequested;
        current.CloseRequested -= OnCloseRequested;
        current.Dispose();
        _presenter.Close();
    }
}
