using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Swiftlet.Application.Abstractions;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Settings;
using Swiftlet.Infrastructure.Persistence.Encryption;

namespace Swiftlet.Desktop.ViewModels;

/// <summary>
/// The view model of the settings window; every change is saved immediately.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    /// <summary>
    /// The settings store.
    /// </summary>
    private readonly IAppSettings _settings;

    /// <summary>
    /// The localizer.
    /// </summary>
    private readonly ILocalizer _localizer;

    /// <summary>
    /// The saved rules that allow remote images for senders.
    /// </summary>
    private readonly IRemoteImageRules _remoteImageRules;

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsViewModel"/> class.
    /// </summary>
    /// <param name="settings">The settings store.</param>
    /// <param name="localizer">The localizer.</param>
    /// <param name="remoteImageRules">The saved rules that allow remote images for senders.</param>
    /// <param name="protection">The protection of the local data; optional.</param>
    public SettingsViewModel(
        IAppSettings settings,
        ILocalizer localizer,
        IRemoteImageRules remoteImageRules,
        IDatabaseProtection? protection = null)
    {
        _settings = settings;
        _localizer = localizer;
        _remoteImageRules = remoteImageRules;
        RemoveServerDeletedMessages = settings.RemoveServerDeletedMessages;
        DownloadFullHistory = settings.DownloadFullHistory;
        DownloadAllMail = settings.DownloadAllMail;
        RefreshRemoteImageRules();
        DatabaseStatusText = protection?.Status switch
        {
            DatabaseProtectionStatus.Encrypted => localizer["Settings_DatabaseEncrypted"],
            DatabaseProtectionStatus.NotEncryptedNoKeychain => localizer["Settings_DatabaseNoKeychain"],
            DatabaseProtectionStatus.PartlyEncrypted => localizer["Settings_DatabasePartly"],
            _ => null,
        };
    }

    /// <summary>
    /// Gets the text saying whether the local data is encrypted, or <see langword="null"/> when unknown.
    /// </summary>
    public string? DatabaseStatusText { get; }

    /// <summary>
    /// The largest number of rules shown at once; the filter box narrows longer lists.
    /// </summary>
    private const int MaxVisibleRules = 200;

    /// <summary>
    /// Raised when the window should close.
    /// </summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// Gets or sets a value indicating whether messages deleted on the server are removed from the local cache.
    /// </summary>
    [ObservableProperty]
    public partial bool RemoveServerDeletedMessages { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the full message history is downloaded in the background.
    /// </summary>
    [ObservableProperty]
    public partial bool DownloadFullHistory { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the "All Mail" folder is downloaded too.
    /// </summary>
    [ObservableProperty]
    public partial bool DownloadAllMail { get; set; }

    /// <summary>
    /// Gets or sets the localized error of the last failed save, or <see langword="null"/>.
    /// </summary>
    [ObservableProperty]
    public partial string? ErrorText { get; set; }

    /// <summary>
    /// Saves the new value as soon as the checkbox changes.
    /// </summary>
    /// <param name="value">The new value.</param>
    partial void OnRemoveServerDeletedMessagesChanged(bool value)
    {
        if (value == _settings.RemoveServerDeletedMessages)
        {
            return;
        }

        ErrorText = null;
        try
        {
            _settings.SetRemoveServerDeletedMessages(value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file could not be written; show it and put the checkbox back so it matches what is stored.
            ErrorText = _localizer.Format("Error_Format", _localizer["Settings_SaveFailed"], ex.Message);
            RemoveServerDeletedMessages = _settings.RemoveServerDeletedMessages;
        }
    }

    /// <summary>
    /// Saves the new value as soon as the checkbox changes.
    /// </summary>
    /// <param name="value">The new value.</param>
    partial void OnDownloadFullHistoryChanged(bool value)
    {
        if (value == _settings.DownloadFullHistory)
        {
            return;
        }

        ErrorText = null;
        try
        {
            _settings.SetDownloadFullHistory(value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file could not be written; show it and put the checkbox back so it matches what is stored.
            ErrorText = _localizer.Format("Error_Format", _localizer["Settings_SaveFailed"], ex.Message);
            DownloadFullHistory = _settings.DownloadFullHistory;
        }
    }

    /// <summary>
    /// Saves the new value as soon as the checkbox changes.
    /// </summary>
    /// <param name="value">The new value.</param>
    partial void OnDownloadAllMailChanged(bool value)
    {
        if (value == _settings.DownloadAllMail)
        {
            return;
        }

        ErrorText = null;
        try
        {
            _settings.SetDownloadAllMail(value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file could not be written; show it and put the checkbox back so it matches what is stored.
            ErrorText = _localizer.Format("Error_Format", _localizer["Settings_SaveFailed"], ex.Message);
            DownloadAllMail = _settings.DownloadAllMail;
        }
    }

    /// <summary>
    /// Gets the saved remote image rules that match the filter (at most the first 200).
    /// </summary>
    public ObservableCollection<string> RemoteImageRules { get; } = [];

    /// <summary>
    /// Gets or sets the text that narrows the rule list.
    /// </summary>
    [ObservableProperty]
    public partial string RemoteImageFilter { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the rule selected in the list.
    /// </summary>
    [ObservableProperty]
    public partial string? SelectedRemoteImageRule { get; set; }

    /// <summary>
    /// Gets or sets the address or domain typed for a new rule.
    /// </summary>
    [ObservableProperty]
    public partial string NewRemoteImageRule { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the text describing how many rules match, for example "3 of 250".
    /// </summary>
    [ObservableProperty]
    public partial string RemoteImageRuleCountText { get; set; } = string.Empty;

    /// <summary>
    /// Reloads the visible rules when the filter changes.
    /// </summary>
    /// <param name="value">The new filter text.</param>
    partial void OnRemoteImageFilterChanged(string value)
    {
        RefreshRemoteImageRules();
    }

    /// <summary>
    /// Validates and saves the typed rule.
    /// </summary>
    /// <returns>A task that completes when the rule is saved or the problem is shown.</returns>
    [RelayCommand]
    private async Task AddRemoteImageRuleAsync()
    {
        ErrorText = null;
        if (!RemoteImageRule.TryNormalize(NewRemoteImageRule, out var rule))
        {
            ErrorText = _localizer["Settings_RemoteImagesInvalid"];
            return;
        }

        try
        {
            await _remoteImageRules.AddAsync(rule);
            NewRemoteImageRule = string.Empty;
            RefreshRemoteImageRules();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // This is the UI boundary: show the failure in the window instead of crashing.
            ErrorText = _localizer.Format("Error_Format", _localizer["Settings_SaveFailed"], ex.Message);
        }
    }

    /// <summary>
    /// Deletes the selected rule.
    /// </summary>
    /// <returns>A task that completes when the rule is deleted or the problem is shown.</returns>
    [RelayCommand]
    private async Task RemoveRemoteImageRuleAsync()
    {
        if (SelectedRemoteImageRule is not { } rule)
        {
            return;
        }

        ErrorText = null;
        try
        {
            await _remoteImageRules.RemoveAsync(rule);
            RefreshRemoteImageRules();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // This is the UI boundary: show the failure in the window instead of crashing.
            ErrorText = _localizer.Format("Error_Format", _localizer["Settings_SaveFailed"], ex.Message);
        }
    }

    /// <summary>
    /// Fills the visible rule list from the saved rules and the filter.
    /// </summary>
    private void RefreshRemoteImageRules()
    {
        var (rules, total) = _remoteImageRules.Search(RemoteImageFilter, MaxVisibleRules);
        RemoteImageRules.Clear();
        foreach (var rule in rules)
        {
            RemoteImageRules.Add(rule);
        }

        RemoteImageRuleCountText = _localizer.Format("Settings_RemoteImagesCount", rules.Count, total);
    }

    /// <summary>
    /// Asks the window to close.
    /// </summary>
    [RelayCommand]
    private void Close()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}
