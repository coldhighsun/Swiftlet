using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Swiftlet.Application;
using Swiftlet.Desktop.Hosting;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Services;

namespace Swiftlet.Desktop.ViewModels.Settings;

/// <summary>
/// The view model of the "About" page of the settings window.
/// </summary>
/// <param name="dialogs">Opens folders.</param>
/// <param name="localizer">The localizer.</param>
/// <param name="paths">The folders the application writes to.</param>
/// <param name="logger">The logger; optional.</param>
public sealed partial class AboutViewModel(
    IDialogService dialogs,
    ILocalizer localizer,
    AppPaths paths,
    ILogger<AboutViewModel>? logger = null) : ObservableObject
{
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
