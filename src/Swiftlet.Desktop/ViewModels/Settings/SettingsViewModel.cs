using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;
using System.Security;
using Avalonia.Media;
using Avalonia.Media.Immutable;
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
    /// Registers the application to start at sign-in; <see langword="null"/> when that cannot be chosen.
    /// </summary>
    private readonly IAutoStart? _autoStart;

    /// <summary>
    /// What the system last reported about starting at sign-in, so that showing it does not write it back.
    /// </summary>
    private bool _storedStartAtLogin;

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
    /// <param name="ai">The AI page; optional.</param>
    /// <param name="autoStart">Registers the application to start at sign-in; optional.</param>
    public SettingsViewModel(
        IAppSettings settings,
        ILocalizer localizer,
        IRemoteImageRules remoteImageRules,
        IDatabaseProtection? protection = null,
        IThemeApplier? themeApplier = null,
        LanguageSelectorViewModel? languageSelector = null,
        AboutViewModel? about = null,
        AiSettingsViewModel? ai = null,
        IAutoStart? autoStart = null)
    {
        _settings = settings;
        _localizer = localizer;
        _autoStart = autoStart;
        ReadStartAtLogin();
        _remoteImageRules = remoteImageRules;
        RemoveServerDeletedMessages = settings.RemoveServerDeletedMessages;
        DownloadFullHistory = settings.DownloadFullHistory;
        CloseToTray = settings.CloseToTray;
        ShowNewMailNotifications = settings.ShowNewMailNotifications;
        StartMinimized = settings.StartMinimized;
        UseSoftwareRendering = !settings.UseHardwareRendering;
        ReleaseWebViewWhenHidden = settings.ReleaseWebViewWhenHidden;
        DisableAnimations = !settings.EnableAnimations;
        RefreshRemoteImageRules();
        _protection = protection;
        _themeApplier = themeApplier;
        LanguageSelector = languageSelector;
        About = about;
        Ai = ai;
        about?.Attach();
        ThemeOptions =
        [
            new ChoiceOptionViewModel(ThemePreference.System, () => localizer["Settings_ThemeSystem"]),
            new ChoiceOptionViewModel(ThemePreference.Light, () => localizer["Settings_ThemeLight"]),
            new ChoiceOptionViewModel(ThemePreference.Dark, () => localizer["Settings_ThemeDark"]),
        ];
        SelectedTheme = ThemeOptions.First(o => o.Code == settings.Theme);
        ColorSchemeOptions =
        [
            Scheme(ColorScheme.Ocean, () => localizer["Settings_ColorSchemeOcean"]),
            Scheme(ColorScheme.Indigo, () => localizer["Settings_ColorSchemeIndigo"]),
            Scheme(ColorScheme.Forest, () => localizer["Settings_ColorSchemeForest"]),
            Scheme(ColorScheme.Sand, () => localizer["Settings_ColorSchemeSand"]),
            Scheme(ColorScheme.Graphite, () => localizer["Settings_ColorSchemeGraphite"]),
        ];
        SelectedColorScheme = ColorSchemeOptions.First(o => o.Code == settings.ColorScheme);
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
    /// Gets the AI page, or <see langword="null"/> when the window has none.
    /// </summary>
    public AiSettingsViewModel? Ai { get; }

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
    /// Gets the available color schemes.
    /// </summary>
    public IReadOnlyList<ChoiceOptionViewModel> ColorSchemeOptions { get; }

    /// <summary>
    /// Gets or sets the selected color scheme.
    /// </summary>
    [ObservableProperty]
    public partial ChoiceOptionViewModel SelectedColorScheme { get; set; }

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
        About?.Detach();
        Ai?.Detach();
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
    /// Saves and applies a newly selected color scheme.
    /// </summary>
    /// <param name="value">The new selection.</param>
    partial void OnSelectedColorSchemeChanged(ChoiceOptionViewModel value)
    {
        if (value is null || value.Code == _settings.ColorScheme)
        {
            return;
        }

        ErrorText = null;
        try
        {
            _settings.SetColorScheme(value.Code);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file could not be written; show it and put the picker back so it matches what is stored.
            ErrorText = _localizer.Format("Error_Format", _localizer["Settings_SaveFailed"], ex.Message);
            SelectedColorScheme = ColorSchemeOptions.First(o => o.Code == _settings.ColorScheme);
            return;
        }

        _themeApplier?.ApplyColorScheme(value.Code);
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
    /// Builds an entry of the color scheme picker.
    /// </summary>
    /// <param name="code">One of the <see cref="ColorScheme"/> values.</param>
    /// <param name="displayName">Produces the display name in the current language.</param>
    /// <returns>The entry with the color of the scheme beside it.</returns>
    private static ChoiceOptionViewModel Scheme(string code, Func<string> displayName)
    {
        return new ChoiceOptionViewModel(code, displayName)
        {
            Swatch = new ImmutableSolidColorBrush(Color.Parse(ColorScheme.Swatch(code))),
        };
    }

    /// <summary>
    /// Re-reads the texts built by this view model after the language changed.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var option in ThemeOptions.Concat(ColorSchemeOptions).Concat(TrayUnreadStyleOptions)
            .Concat(ContactNameOrderOptions))
        {
            option.RefreshLanguage();
        }

        About?.RefreshLanguage();
        Ai?.RefreshLanguage();
        OnPropertyChanged(nameof(DatabaseStatusText));
        foreach (var rule in RemoteImageRules)
        {
            rule.RefreshLanguage();
        }

        UpdateRemoteImageRuleTexts();
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
    /// Gets a value indicating whether starting the application at sign-in can be chosen.
    /// </summary>
    public bool IsAutoStartSupported => _autoStart is { IsSupported: true };

    /// <summary>
    /// Gets or sets a value indicating whether the application starts when the user signs in.
    /// </summary>
    [ObservableProperty]
    public partial bool StartAtLogin { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the processor draws the application from the next start on. It is the
    /// inverse of the stored hardware rendering choice, so that every checkbox on the page saves memory when checked.
    /// </summary>
    [ObservableProperty]
    public partial bool UseSoftwareRendering { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the web view is released while the main window is hidden.
    /// </summary>
    [ObservableProperty]
    public partial bool ReleaseWebViewWhenHidden { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user interface stays still. It is the inverse of the stored choice,
    /// so that every checkbox on the page saves resources when checked.
    /// </summary>
    [ObservableProperty]
    public partial bool DisableAnimations { get; set; }

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
    /// Registers or removes the sign-in entry as soon as the checkbox changes.
    /// </summary>
    /// <param name="value">The new value.</param>
    partial void OnStartAtLoginChanged(bool value)
    {
        if (_autoStart is not { IsSupported: true } autoStart || value == _storedStartAtLogin)
        {
            return;
        }

        ErrorText = null;
        try
        {
            autoStart.SetEnabled(value);
            _storedStartAtLogin = value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // The system refused the change; show it and put the checkbox back to what the system reports.
            ReadStartAtLogin();
            ErrorText = _localizer.Format("Error_Format", _localizer["Settings_StartAtLoginFailed"], ex.Message);
        }
    }

    /// <summary>
    /// Shows whether the system currently starts the application at sign-in.
    /// </summary>
    private void ReadStartAtLogin()
    {
        if (_autoStart is not { IsSupported: true } autoStart)
        {
            return;
        }

        try
        {
            _storedStartAtLogin = autoStart.IsEnabled();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            // The window must still open; the last known value stays and the problem is shown.
            ErrorText = _localizer.Format("Error_Format", _localizer["Settings_StartAtLoginFailed"], ex.Message);
        }

        StartAtLogin = _storedStartAtLogin;
    }

    /// <summary>
    /// Saves the new value as soon as the checkbox changes.
    /// </summary>
    /// <param name="value">The new value.</param>
    partial void OnUseSoftwareRenderingChanged(bool value)
    {
        if (value == !_settings.UseHardwareRendering)
        {
            return;
        }

        ErrorText = null;
        try
        {
            _settings.SetUseHardwareRendering(!value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file could not be written; show it and put the checkbox back so it matches what is stored.
            ErrorText = _localizer.Format("Error_Format", _localizer["Settings_SaveFailed"], ex.Message);
            UseSoftwareRendering = !_settings.UseHardwareRendering;
        }
    }

    /// <summary>
    /// Saves the new value as soon as the checkbox changes.
    /// </summary>
    /// <param name="value">The new value.</param>
    partial void OnReleaseWebViewWhenHiddenChanged(bool value)
    {
        if (value == _settings.ReleaseWebViewWhenHidden)
        {
            return;
        }

        ErrorText = null;
        try
        {
            _settings.SetReleaseWebViewWhenHidden(value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file could not be written; show it and put the checkbox back so it matches what is stored.
            ErrorText = _localizer.Format("Error_Format", _localizer["Settings_SaveFailed"], ex.Message);
            ReleaseWebViewWhenHidden = _settings.ReleaseWebViewWhenHidden;
        }
    }

    /// <summary>
    /// Saves the new value as soon as the checkbox changes.
    /// </summary>
    /// <param name="value">The new value.</param>
    partial void OnDisableAnimationsChanged(bool value)
    {
        if (value == !_settings.EnableAnimations)
        {
            return;
        }

        ErrorText = null;
        try
        {
            _settings.SetEnableAnimations(!value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The file could not be written; show it and put the checkbox back so it matches what is stored.
            ErrorText = _localizer.Format("Error_Format", _localizer["Settings_SaveFailed"], ex.Message);
            DisableAnimations = !_settings.EnableAnimations;
        }
    }

    /// <summary>
    /// Gets the saved remote image rules that match the filter (at most the first 200).
    /// </summary>
    public ObservableCollection<RemoteImageRuleViewModel> RemoteImageRules { get; } = [];

    /// <summary>
    /// Gets or sets the text that narrows the rule list.
    /// </summary>
    [ObservableProperty]
    public partial string RemoteImageFilter { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the rule selected in the list.
    /// </summary>
    [ObservableProperty]
    public partial RemoteImageRuleViewModel? SelectedRemoteImageRule { get; set; }

    /// <summary>
    /// Gets or sets the address or domain typed for a new rule.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddRemoteImageRuleCommand))]
    public partial string NewRemoteImageRule { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the localized problem of the last change to the rules, or <see langword="null"/>.
    /// </summary>
    [ObservableProperty]
    public partial string? RemoteImageError { get; set; }

    /// <summary>
    /// Gets or sets the text describing how many rules are listed out of those that match, for example "200 of 250".
    /// </summary>
    [ObservableProperty]
    public partial string RemoteImageRuleCountText { get; set; } = string.Empty;

    /// <summary>
    /// Gets a value indicating whether the search box is useful: there are rules, or a filter that can be cleared.
    /// </summary>
    public bool ShowRemoteImageSearch => _ruleTotal > 0 || !string.IsNullOrEmpty(RemoteImageFilter);

    /// <summary>
    /// Gets a value indicating whether any rule is listed.
    /// </summary>
    public bool HasVisibleRemoteImageRules => RemoteImageRules.Count > 0;

    /// <summary>
    /// Gets a value indicating whether more rules match than are listed.
    /// </summary>
    public bool IsRemoteImageListTruncated => RemoteImageRules.Count < _ruleTotal;

    /// <summary>
    /// Gets the text shown in place of an empty list: nothing is saved, or nothing matches the filter.
    /// </summary>
    public string RemoteImageEmptyText => string.IsNullOrWhiteSpace(RemoteImageFilter)
        ? _localizer["Settings_RemoteImagesEmpty"]
        : _localizer["Settings_RemoteImagesNoMatch"];

    /// <summary>
    /// Reloads the visible rules when the filter changes.
    /// </summary>
    /// <param name="value">The new filter text.</param>
    partial void OnRemoteImageFilterChanged(string value)
    {
        RefreshRemoteImageRules();
    }

    /// <summary>
    /// Removes the shown problem once the typed rule changes.
    /// </summary>
    /// <param name="value">The new text.</param>
    partial void OnNewRemoteImageRuleChanged(string value)
    {
        RemoteImageError = null;
    }

    /// <summary>
    /// Says whether there is anything to add.
    /// </summary>
    /// <returns><see langword="true"/> when something is typed.</returns>
    private bool CanAddRemoteImageRule()
    {
        return !string.IsNullOrWhiteSpace(NewRemoteImageRule);
    }

    /// <summary>
    /// Validates and saves the typed rule.
    /// </summary>
    /// <returns>A task that completes when the rule is saved or the problem is shown.</returns>
    [RelayCommand(CanExecute = nameof(CanAddRemoteImageRule))]
    private async Task AddRemoteImageRuleAsync()
    {
        RemoteImageError = null;
        if (!RemoteImageRule.TryNormalize(NewRemoteImageRule, out var rule))
        {
            RemoteImageError = _localizer["Settings_RemoteImagesInvalid"];
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
            RemoteImageError = _localizer.Format("Error_Format", _localizer["Settings_SaveFailed"], ex.Message);
        }
    }

    /// <summary>
    /// Deletes a listed rule.
    /// </summary>
    /// <param name="item">The rule to delete; <see langword="null"/> does nothing.</param>
    /// <returns>A task that completes when the rule is deleted or the problem is shown.</returns>
    [RelayCommand]
    private async Task RemoveRemoteImageRuleAsync(RemoteImageRuleViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        RemoteImageError = null;
        try
        {
            await _remoteImageRules.RemoveAsync(item.Rule);
            RefreshRemoteImageRules();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // This is the UI boundary: show the failure in the window instead of crashing.
            RemoteImageError = _localizer.Format("Error_Format", _localizer["Settings_SaveFailed"], ex.Message);
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
            RemoteImageRules.Add(new RemoteImageRuleViewModel(rule, _localizer));
        }

        _ruleTotal = total;
        UpdateRemoteImageRuleTexts();
        OnPropertyChanged(nameof(ShowRemoteImageSearch));
        OnPropertyChanged(nameof(HasVisibleRemoteImageRules));
        OnPropertyChanged(nameof(IsRemoteImageListTruncated));
    }

    /// <summary>
    /// Rewrites the texts about the listed rules.
    /// </summary>
    private void UpdateRemoteImageRuleTexts()
    {
        RemoteImageRuleCountText = _localizer.Format("Settings_RemoteImagesCount", RemoteImageRules.Count, _ruleTotal);
        OnPropertyChanged(nameof(RemoteImageEmptyText));
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
