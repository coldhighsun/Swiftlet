using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Swiftlet.Desktop.Formatting;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Services;

namespace Swiftlet.Desktop.ViewModels.Update;

/// <summary>
/// What the update window is showing.
/// </summary>
public enum UpdateStep
{
    /// <summary>
    /// Asking whether to update.
    /// </summary>
    Prompt,

    /// <summary>
    /// Downloading the installer.
    /// </summary>
    Downloading,

    /// <summary>
    /// The last attempt failed; it can be tried again.
    /// </summary>
    Failed,
}

/// <summary>
/// The view model of the window offering a newer version.
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    /// <summary>
    /// The longest release notes shown, so one oversized release cannot make the window unwieldy.
    /// </summary>
    internal const int MaxNotesLength = 4000;

    /// <summary>
    /// The update service.
    /// </summary>
    private readonly IUpdateService _updates;

    /// <summary>
    /// The localizer.
    /// </summary>
    private readonly ILocalizer _localizer;

    /// <summary>
    /// The logger.
    /// </summary>
    private readonly ILogger? _logger;

    /// <summary>
    /// Cancels the running download, or <see langword="null"/> when none runs.
    /// </summary>
    private Action? _cancelRunning;

    /// <summary>
    /// Whether the window has closed, so a download ending late changes nothing.
    /// </summary>
    private bool _detached;

    /// <summary>
    /// Initializes a new instance of the <see cref="UpdateViewModel"/> class.
    /// </summary>
    /// <param name="updates">The update service.</param>
    /// <param name="available">The version offered.</param>
    /// <param name="currentVersion">The running version.</param>
    /// <param name="localizer">The localizer.</param>
    /// <param name="logger">The logger; optional.</param>
    public UpdateViewModel(
        IUpdateService updates,
        AvailableVersion available,
        string currentVersion,
        ILocalizer localizer,
        ILogger<UpdateViewModel>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(available);
        ArgumentNullException.ThrowIfNull(localizer);
        _updates = updates;
        _localizer = localizer;
        _logger = logger;
        CanInstall = updates.CanInstall;
        Header = localizer.Format("Update_Header", available.Version);
        CurrentVersionText = localizer.Format("Update_CurrentVersion", currentVersion);
        var notes = available.Notes?.Trim() ?? string.Empty;
        Notes = notes.Length > MaxNotesLength ? notes[..MaxNotesLength] + "…" : notes;
        ReleasePage = available.ReleasePage;
        ProgressText = string.Empty;
        updates.ProgressChanged += OnProgressChanged;
    }

    /// <summary>
    /// Raised when the window should close.
    /// </summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// Gets the headline naming the new version.
    /// </summary>
    public string Header { get; }

    /// <summary>
    /// Gets the line naming the running version.
    /// </summary>
    public string CurrentVersionText { get; }

    /// <summary>
    /// Gets the release notes, empty when there are none.
    /// </summary>
    public string Notes { get; }

    /// <summary>
    /// Gets a value indicating whether there are release notes.
    /// </summary>
    public bool HasNotes => Notes.Length > 0;

    /// <summary>
    /// Gets the page of the release.
    /// </summary>
    public Uri ReleasePage { get; }

    /// <summary>
    /// Gets a value indicating whether this copy can install the version itself.
    /// </summary>
    public bool CanInstall { get; }

    /// <summary>
    /// Gets the text saying what updating does, or why it has to be done by hand.
    /// </summary>
    public string InfoText => CanInstall ? _localizer["Update_InstallNote"] : _localizer["Update_ManualNote"];

    /// <summary>
    /// Gets or sets what the window shows.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDownloading))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    [NotifyPropertyChangedFor(nameof(ShowInfo))]
    [NotifyPropertyChangedFor(nameof(ShowUpdateButton))]
    [NotifyPropertyChangedFor(nameof(UpdateButtonText))]
    [NotifyPropertyChangedFor(nameof(ShowSkipButton))]
    [NotifyPropertyChangedFor(nameof(ShowLaterButton))]
    [NotifyPropertyChangedFor(nameof(LaterButtonText))]
    [NotifyPropertyChangedFor(nameof(ShowReleaseLink))]
    public partial UpdateStep Step { get; set; }

    /// <summary>
    /// Gets a value indicating whether a download runs.
    /// </summary>
    public bool IsDownloading => Step == UpdateStep.Downloading;

    /// <summary>
    /// Gets a value indicating whether the last attempt failed.
    /// </summary>
    public bool IsFailed => Step == UpdateStep.Failed;

    /// <summary>
    /// Gets a value indicating whether the explanation is shown.
    /// </summary>
    public bool ShowInfo => Step == UpdateStep.Prompt;

    /// <summary>
    /// Gets a value indicating whether the button that updates is shown.
    /// </summary>
    public bool ShowUpdateButton => CanInstall && !IsDownloading;

    /// <summary>
    /// Gets the caption of the button that updates.
    /// </summary>
    public string UpdateButtonText => IsFailed ? _localizer["Update_Retry"] : _localizer["Update_Now"];

    /// <summary>
    /// Gets a value indicating whether the version can be skipped.
    /// </summary>
    public bool ShowSkipButton => Step == UpdateStep.Prompt;

    /// <summary>
    /// Gets a value indicating whether the button that closes the window is shown.
    /// </summary>
    public bool ShowLaterButton => !IsDownloading;

    /// <summary>
    /// Gets the caption of the button that closes the window.
    /// </summary>
    public string LaterButtonText => IsFailed ? _localizer["Common_Close"] : _localizer["Update_Later"];

    /// <summary>
    /// Gets a value indicating whether the link to the release page is shown.
    /// </summary>
    public bool ShowReleaseLink => !IsDownloading && (IsFailed || !CanInstall);

    /// <summary>
    /// Gets or sets the text saying why the update failed.
    /// </summary>
    [ObservableProperty]
    public partial string? ErrorText { get; set; }

    /// <summary>
    /// Gets or sets the text describing the download.
    /// </summary>
    [ObservableProperty]
    public partial string ProgressText { get; set; }

    /// <summary>
    /// Gets or sets how much of the download is done, from 0 to 100.
    /// </summary>
    [ObservableProperty]
    public partial double ProgressValue { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the size of the download is unknown.
    /// </summary>
    [ObservableProperty]
    public partial bool IsProgressIndeterminate { get; set; }

    /// <summary>
    /// Stops the download and stops listening; call it when the window has closed.
    /// </summary>
    public void Detach()
    {
        _detached = true;
        _updates.ProgressChanged -= OnProgressChanged;
        _cancelRunning?.Invoke();
    }

    /// <summary>
    /// Downloads the installer and starts it.
    /// </summary>
    /// <returns>A task that completes when the installer was started or the attempt ended otherwise.</returns>
    [RelayCommand]
    private async Task UpdateAsync()
    {
        if (!CanInstall || IsDownloading)
        {
            return;
        }

        ErrorText = null;
        ProgressValue = 0;
        IsProgressIndeterminate = true;
        ProgressText = _localizer["Update_Starting"];
        Step = UpdateStep.Downloading;
        using var cancellation = new CancellationTokenSource();
        _cancelRunning = cancellation.Cancel;
        try
        {
            var started = await _updates.InstallAsync(cancellation.Token);
            if (_detached)
            {
                return;
            }

            if (started)
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            ShowFailure(_updates.InstallError is { } error
                ? _localizer.Format("Update_Failed", error.Message)
                : _localizer["Update_NotStarted"]);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_detached)
            {
                Step = UpdateStep.Prompt;
            }
        }
        catch (Exception ex)
        {
            // This is the UI boundary: whatever else goes wrong, the window must not stay in the downloading state.
            _logger?.LogWarning(ex, "The update could not be installed");
            if (!_detached)
            {
                ShowFailure(_localizer.Format("Update_Failed", ex.Message));
            }
        }
        finally
        {
            _cancelRunning = null;
        }
    }

    /// <summary>
    /// Stops the running download.
    /// </summary>
    [RelayCommand]
    private void CancelDownload()
    {
        _cancelRunning?.Invoke();
    }

    /// <summary>
    /// Stops offering this version and closes the window.
    /// </summary>
    /// <returns>A task that completes when the choice is recorded.</returns>
    [RelayCommand]
    private async Task SkipAsync()
    {
        // A choice that cannot be saved is logged by the service and only means the version is offered again.
        await _updates.SkipAsync();
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Closes the window.
    /// </summary>
    [RelayCommand]
    private void Close()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Shows why the update failed.
    /// </summary>
    /// <param name="message">The localized message.</param>
    private void ShowFailure(string message)
    {
        ErrorText = message;
        Step = UpdateStep.Failed;
    }

    /// <summary>
    /// Shows the latest download progress.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private void OnProgressChanged(object? sender, EventArgs e)
    {
        if (!IsDownloading || _updates.Progress is not { } progress)
        {
            return;
        }

        var received = FileSizeFormatter.Format(progress.BytesReceived);
        var speed = FileSizeFormatter.Format((long)progress.BytesPerSecond);
        if (progress.TotalBytes is { } total and > 0)
        {
            IsProgressIndeterminate = false;
            ProgressValue = Math.Min(100d, progress.BytesReceived * 100d / total);
            ProgressText = _localizer.Format("Update_Progress", received, FileSizeFormatter.Format(total), speed);
        }
        else
        {
            ProgressText = _localizer.Format("Update_ProgressUnknownTotal", received, speed);
        }
    }
}
