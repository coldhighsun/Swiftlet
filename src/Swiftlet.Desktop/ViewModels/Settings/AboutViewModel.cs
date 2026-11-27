using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Swiftlet.Application;
using Swiftlet.Desktop.Hosting;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Services;
using Swiftlet.Desktop.Settings;

namespace Swiftlet.Desktop.ViewModels.Settings;

/// <summary>
/// The view model of the "About" page of the settings window.
/// </summary>
/// <param name="dialogs">Opens folders.</param>
/// <param name="localizer">The localizer.</param>
/// <param name="paths">The folders the application writes to.</param>
/// <param name="logger">The logger; optional.</param>
/// <param name="updates">Looks for new versions; optional.</param>
/// <param name="settings">The settings, which switch the automatic checks; optional.</param>
public sealed partial class AboutViewModel(
    IDialogService dialogs,
    ILocalizer localizer,
    AppPaths paths,
    ILogger<AboutViewModel>? logger = null,
    IUpdateService? updates = null,
    IAppSettings? settings = null) : ObservableObject
{
    /// <summary>
    /// Whether the page listens to the update service.
    /// </summary>
    private bool _attached;

    /// <summary>
    /// Whether new versions are looked for automatically, as shown by the checkbox.
    /// </summary>
    private bool _checkForUpdates = settings?.CheckForUpdates ?? false;

    /// <summary>
    /// Gets a value indicating whether the page offers updates; development builds look for none.
    /// </summary>
    public bool HasUpdates => updates is { IsSupported: true } && settings is not null;

    /// <summary>
    /// Gets or sets a value indicating whether new versions are looked for automatically; saved at once.
    /// </summary>
    public bool CheckForUpdates
    {
        get => _checkForUpdates;
        set
        {
            if (settings is null || value == _checkForUpdates)
            {
                return;
            }

            ErrorText = null;
            try
            {
                settings.SetCheckForUpdates(value);
                _checkForUpdates = value;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The file could not be written; show it, and the notification below puts the checkbox back.
                ErrorText = localizer.Format("Error_Format", localizer["Settings_SaveFailed"], ex.Message);
            }

            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets a value indicating whether a newer version is known.
    /// </summary>
    public bool HasAvailableUpdate => updates?.Available is not null;

    /// <summary>
    /// Gets the localized line saying what the update check found.
    /// </summary>
    public string UpdateStatusText
    {
        get
        {
            if (updates is null)
            {
                return string.Empty;
            }

            var version = updates.Available?.Version;
            if (updates.Status == UpdateStatus.Checking)
            {
                return localizer["About_Checking"];
            }

            if (updates.CheckError is { } error)
            {
                return localizer.Format("About_CheckFailed", error.Message);
            }

            return updates.Status switch
            {
                UpdateStatus.Installing => localizer.Format("About_Downloading", version ?? string.Empty),
                UpdateStatus.Available => localizer.Format("About_UpdateAvailable", version ?? string.Empty),
                UpdateStatus.UpToDate => localizer["About_UpToDate"],
                _ => string.Empty,
            };
        }
    }

    /// <summary>
    /// Starts following the update service; call it when the page is about to be shown.
    /// </summary>
    public void Attach()
    {
        if (updates is not null && !_attached)
        {
            _attached = true;
            updates.Changed += OnUpdatesChanged;
        }
    }

    /// <summary>
    /// Stops following the update service, which outlives the window.
    /// </summary>
    public void Detach()
    {
        if (updates is not null && _attached)
        {
            _attached = false;
            updates.Changed -= OnUpdatesChanged;
        }
    }

    /// <summary>
    /// The address of the project, the base of every link on the page.
    /// </summary>
    private const string ProjectAddress = "https://github.com/coldhighsun/Swiftlet";

    /// <summary>
    /// Gets the version of the running application.
    /// </summary>
    public string Version { get; } = AppVersion.Display;

    /// <summary>
    /// Gets the localized line showing the version.
    /// </summary>
    public string VersionText => localizer.Format("About_Version", Version);

    /// <summary>
    /// Gets the home page of the project.
    /// </summary>
    public Uri HomePage { get; } = new(ProjectAddress);

    /// <summary>
    /// Gets the page for reporting problems.
    /// </summary>
    public Uri IssuesPage { get; } = new($"{ProjectAddress}/issues");

    /// <summary>
    /// Gets the license of the application.
    /// </summary>
    public Uri LicensePage { get; } = new($"{ProjectAddress}/blob/main/LICENSE");

    /// <summary>
    /// Gets the notices of the third-party components.
    /// </summary>
    public Uri ThirdPartyNoticesPage { get; } = new($"{ProjectAddress}/blob/main/THIRD-PARTY-NOTICES.md");

    /// <summary>
    /// Gets the privacy policy.
    /// </summary>
    public Uri PrivacyPage { get; } = new($"{ProjectAddress}/blob/main/PRIVACY.md");

    /// <summary>
    /// Gets the terms of use.
    /// </summary>
    public Uri TermsPage { get; } = new($"{ProjectAddress}/blob/main/TERMS.md");

    /// <summary>
    /// Gets or sets the localized error of the last folder that could not be opened, or <see langword="null"/>.
    /// </summary>
    [ObservableProperty]
    public partial string? ErrorText { get; set; }

    /// <summary>
    /// Re-reads the texts built by this view model after the language changed.
    /// </summary>
    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(VersionText));
        if (HasUpdates)
        {
            OnPropertyChanged(nameof(UpdateStatusText));
        }
    }

    /// <summary>
    /// Asks for a newer version now.
    /// </summary>
    /// <returns>A task that completes when the check is done.</returns>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckNowAsync()
    {
        if (updates is not null)
        {
            // Failures are reported by the service through its state, which the page shows.
            await updates.CheckAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Tells whether a check can be started.
    /// </summary>
    /// <returns><see langword="true"/> while nothing else is running.</returns>
    private bool CanCheck()
    {
        return updates is { Status: not (UpdateStatus.Checking or UpdateStatus.Installing) };
    }

    /// <summary>
    /// Opens the update window for the newer version.
    /// </summary>
    /// <returns>A task that completes when the window has closed.</returns>
    [RelayCommand]
    private Task ShowUpdateAsync()
    {
        return dialogs.ShowUpdateAsync();
    }

    /// <summary>
    /// Shows the new state of the update check.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private void OnUpdatesChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(UpdateStatusText));
        OnPropertyChanged(nameof(HasAvailableUpdate));
        CheckNowCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Opens the folder holding the log files.
    /// </summary>
    /// <returns>A task that completes when the folder is open or the problem is shown.</returns>
    [RelayCommand]
    private Task OpenLogFolderAsync()
    {
        return OpenFolderAsync(paths.LogDirectory);
    }

    /// <summary>
    /// Opens the folder holding the application data.
    /// </summary>
    /// <returns>A task that completes when the folder is open or the problem is shown.</returns>
    [RelayCommand]
    private Task OpenDataFolderAsync()
    {
        return OpenFolderAsync(paths.DataDirectory);
    }

    /// <summary>
    /// Opens a folder in the file manager and shows when that did not work.
    /// </summary>
    /// <param name="path">The folder.</param>
    /// <returns>A task that completes when the folder is open or the problem is shown.</returns>
    private async Task OpenFolderAsync(string path)
    {
        ErrorText = null;
        try
        {
            if (await dialogs.OpenFolderAsync(path))
            {
                return;
            }

            logger?.LogWarning("The folder {Path} could not be opened", path);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // This is the UI boundary: a missing or broken file manager must not crash the application.
            logger?.LogWarning(ex, "The folder {Path} could not be opened", path);
        }

        ErrorText = localizer.Format("About_OpenFolderFailed", path);
    }
}
