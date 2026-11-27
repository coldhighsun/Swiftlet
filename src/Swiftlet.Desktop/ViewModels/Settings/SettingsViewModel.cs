using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Swiftlet.Application.Abstractions;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Services;
using Swiftlet.Desktop.Settings;
using Swiftlet.Infrastructure.Persistence.Encryption;

namespace Swiftlet.Desktop.ViewModels.Settings;

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
    /// Applies a newly chosen theme; <see langword="null"/> when there is no running application to change.
    /// </summary>
    private readonly IThemeApplier? _themeApplier;

    /// <summary>
    /// The protection of the local data; <see langword="null"/> when unknown.
    /// </summary>
    private readonly IDatabaseProtection? _protection;

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsViewModel"/> class.
    /// </summary>
    /// <param name="settings">The settings store.</param>
    /// <param name="localizer">The localizer.</param>
    /// <param name="remoteImageRules">The saved rules that allow remote images for senders.</param>
    /// <param name="protection">The protection of the local data; optional.</param>
    /// <param name="themeApplier">Applies a newly chosen theme; optional.</param>
    /// <param name="languageSelector">The language picker shown in the window; optional.</param>
    /// <param name="about">The "About" page; optional.</param>
    public SettingsViewModel(
        IAppSettings settings,
        ILocalizer localizer,
        IRemoteImageRules remoteImageRules,
        IDatabaseProtection? protection = null,
        IThemeApplier? themeApplier = null,
        LanguageSelectorViewModel? languageSelector = null,
        AboutViewModel? about = null)
    {
        _settings = settings;
        _localizer = localizer;
        _remoteImageRules = remoteImageRules;
        RemoveServerDeletedMessages = settings.RemoveServerDeletedMessages;
        DownloadFullHistory = settings.DownloadFullHistory;
        CloseToTray = settings.CloseToTray;
        ShowNewMailNotifications = settings.ShowNewMailNotifications;
        StartMinimized = settings.StartMinimized;
        RefreshRemoteImageRules();
        _protection = protection;
        _themeApplier = themeApplier;
        LanguageSelector = languageSelector;
        About = about;
        ThemeOptions =
        [
            new ChoiceOptionViewModel(ThemePreference.System, () => localizer["Settings_ThemeSystem"]),
            new ChoiceOptionViewModel(ThemePreference.Light, () => localizer["Settings_ThemeLight"]),
            new ChoiceOptionViewModel(ThemePreference.Dark, () => localizer["Settings_ThemeDark"]),
        ];
        SelectedTheme = ThemeOptions.First(o => o.Code == settings.Theme);
        TrayUnreadStyleOptions =
        [
            new ChoiceOptionViewModel(TrayUnreadStyle.Dot, () => localizer["Settings_TrayUnreadDot"]),
            new ChoiceOptionViewModel(TrayUnreadStyle.Blink, () => localizer["Settings_TrayUnreadBlink"]),
        ];
        SelectedTrayUnreadStyle = TrayUnreadStyleOptions.First(o => o.Code == settings.TrayUnreadStyle);
        ContactNameOrderOptions =
        [
            new ChoiceOptionViewModel(ContactNameOrder.Auto, () => localizer["Settings_ContactNameOrderAuto"]),
            new ChoiceOptionViewModel(
                ContactNameOrder.FamilyFirst, () => localizer["Settings_ContactNameOrderFamilyFirst"]),
            new ChoiceOptionViewModel(ContactNameOrder.GivenFirst, () => localizer["Settings_ContactNameOrderGivenFirst"]),
        ];
        SelectedContactNameOrder = ContactNameOrderOptions.First(o => o.Code == settings.ContactNameOrder);
        localizer.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>
    /// Gets the language picker, or <see langword="null"/> when the language cannot be chosen.
    /// </summary>
    public LanguageSelectorViewModel? LanguageSelector { get; }

    /// <summary>
    /// Gets the "About" page, or <see langword="null"/> when the window has none.
    /// </summary>
    public AboutViewModel? About { get; }

    /// <summary>
    /// Gets the available themes.
    /// </summary>
    public IReadOnlyList<ChoiceOptionViewModel> ThemeOptions { get; }

    /// <summary>
    /// Gets or sets the selected theme.
    /// </summary>
    [ObservableProperty]
    public partial ChoiceOptionViewModel SelectedTheme { get; set; }

    /// <summary>
    /// Gets the ways the tray icon can point out unread mail.
    /// </summary>
    public IReadOnlyList<ChoiceOptionViewModel> TrayUnreadStyleOptions { get; }

    /// <summary>
    /// Gets or sets the selected tray icon style.
    /// </summary>
    [ObservableProperty]
    public partial ChoiceOptionViewModel SelectedTrayUnreadStyle { get; set; }

    /// <summary>
    /// Gets the orders in which contact names can be shown.
    /// </summary>
    public IReadOnlyList<ChoiceOptionViewModel> ContactNameOrderOptions { get; }

    /// <summary>
    /// Gets or sets the selected contact name order.
    /// </summary>
    [ObservableProperty]
    public partial ChoiceOptionViewModel SelectedContactNameOrder { get; set; }

    /// <summary>
    /// Gets the text saying whether the local data is encrypted, or <see langword="null"/> when unknown.
    /// </summary>
    public string? DatabaseStatusText => _protection?.Status switch
    {
        DatabaseProtectionStatus.Encrypted => _localizer["Settings_DatabaseEncrypted"],
        DatabaseProtectionStatus.NotEncryptedNoKeychain => _localizer["Settings_DatabaseNoKeychain"],
        DatabaseProtectionStatus.PartlyEncrypted => _localizer["Settings_DatabasePartly"],
        _ => null,
    };

    /// <summary>
    /// Stops listening to the localizer, which outlives the window; call it when the window has closed.
    /// </summary>
    public void Detach()
    {
        _localizer.LanguageChanged -= OnLanguageChanged;
    }

    /// <summary>
    /// Saves and applies a newly selected theme.
    /// </summary>
    /// <param name="value">The new selection.</param>
    partial void OnSelectedThemeChanged(ChoiceOptionViewModel value)
    {
        if (value is null || value.Code == _settings.Theme)
        {
            return;
        }

        ErrorText = null;
        try
        {
            _settings.SetTheme(value.Code);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file could not be written; show it and put the picker back so it matches what is stored.
            ErrorText = _localizer.Format("Error_Format", _localizer["Settings_SaveFailed"], ex.Message);
            SelectedTheme = ThemeOptions.First(o => o.Code == _settings.Theme);
            return;
        }

        _themeApplier?.Apply(value.Code);
    }

    /// <summary>
    /// Saves a newly selected tray icon style.
    /// </summary>
    /// <param name="value">The new selection.</param>
    partial void OnSelectedTrayUnreadStyleChanged(ChoiceOptionViewModel value)
    {
        if (value is null || value.Code == _settings.TrayUnreadStyle)
        {
            return;
        }

        ErrorText = null;
        try
        {
            _settings.SetTrayUnreadStyle(value.Code);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file could not be written; show it and put the picker back so it matches what is stored.
            ErrorText = _localizer.Format("Error_Format", _localizer["Settings_SaveFailed"], ex.Message);
            SelectedTrayUnreadStyle = TrayUnreadStyleOptions.First(o => o.Code == _settings.TrayUnreadStyle);
        }
    }

    /// <summary>
    /// Saves a newly selected contact name order.
    /// </summary>
    /// <param name="value">The new selection.</param>
    partial void OnSelectedContactNameOrderChanged(ChoiceOptionViewModel value)
    {
        if (value is null || value.Code == _settings.ContactNameOrder)
        {
            return;
        }

        ErrorText = null;
        try
        {
            _settings.SetContactNameOrder(value.Code);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file could not be written; show it and put the picker back so it matches what is stored.
            ErrorText = _localizer.Format("Error_Format", _localizer["Settings_SaveFailed"], ex.Message);
            SelectedContactNameOrder = ContactNameOrderOptions.First(o => o.Code == _settings.ContactNameOrder);
        }
    }

    /// <summary>
    /// Re-reads the texts built by this view model after the language changed.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var option in ThemeOptions.Concat(TrayUnreadStyleOptions).Concat(ContactNameOrderOptions))
        {
            option.RefreshLanguage();
        }

        About?.RefreshLanguage();
        OnPropertyChanged(nameof(DatabaseStatusText));
        UpdateRemoteImageRuleCount();
    }

    /// <summary>
    /// How many saved rules match the filter.
    /// </summary>
    private int _ruleTotal;

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
    /// Gets or sets a value indicating whether closing the main window hides it in the tray instead of quitting.
    /// </summary>
    [ObservableProperty]
    public partial bool CloseToTray { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether newly arrived mail is announced with a pop-up.
    /// </summary>
    [ObservableProperty]
    public partial bool ShowNewMailNotifications { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the application starts without showing the main window.
    /// </summary>
    [ObservableProperty]
    public partial bool StartMinimized { get; set; }

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
    partial void OnCloseToTrayChanged(bool value)
    {
        if (value == _settings.CloseToTray)
        {
            return;
        }

        ErrorText = null;
        try
        {
            _settings.SetCloseToTray(value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file could not be written; show it and put the checkbox back so it matches what is stored.
            ErrorText = _localizer.Format("Error_Format", _localizer["Settings_SaveFailed"], ex.Message);
            CloseToTray = _settings.CloseToTray;
        }
    }

    /// <summary>
    /// Saves the new value as soon as the checkbox changes.
    /// </summary>
    /// <param name="value">The new value.</param>
    partial void OnShowNewMailNotificationsChanged(bool value)
    {
        if (value == _settings.ShowNewMailNotifications)
        {
            return;
        }

        ErrorText = null;
        try
        {
            _settings.SetShowNewMailNotifications(value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file could not be written; show it and put the checkbox back so it matches what is stored.
            ErrorText = _localizer.Format("Error_Format", _localizer["Settings_SaveFailed"], ex.Message);
            ShowNewMailNotifications = _settings.ShowNewMailNotifications;
        }
    }

    /// <summary>
    /// Saves the new value as soon as the checkbox changes.
    /// </summary>
    /// <param name="value">The new value.</param>
    partial void OnStartMinimizedChanged(bool value)
    {
        if (value == _settings.StartMinimized)
        {
            return;
        }

        ErrorText = null;
        try
        {
            _settings.SetStartMinimized(value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file could not be written; show it and put the checkbox back so it matches what is stored.
            ErrorText = _localizer.Format("Error_Format", _localizer["Settings_SaveFailed"], ex.Message);
            StartMinimized = _settings.StartMinimized;
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

        _ruleTotal = total;
        UpdateRemoteImageRuleCount();
    }

    /// <summary>
    /// Describes how many rules are listed out of those that match.
    /// </summary>
    private void UpdateRemoteImageRuleCount()
    {
        RemoteImageRuleCountText = _localizer.Format("Settings_RemoteImagesCount", RemoteImageRules.Count, _ruleTotal);
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
