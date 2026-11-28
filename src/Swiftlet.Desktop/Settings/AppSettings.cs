using System.Text.Json;
using System.Text.Json.Serialization;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Ai;
using Swiftlet.Desktop.Localization;

namespace Swiftlet.Desktop.Settings;

/// <summary>
/// The settings the user can change in the settings window.
/// </summary>
public interface IAppSettings : ISyncSettings
{
    /// <summary>
    /// Changes whether messages deleted on the server are removed locally, and saves the choice.
    /// </summary>
    /// <param name="value">The new value.</param>
    void SetRemoveServerDeletedMessages(bool value);

    /// <summary>
    /// Changes whether the full message history is downloaded in the background, and saves the choice.
    /// </summary>
    /// <param name="value">The new value.</param>
    void SetDownloadFullHistory(bool value);

    /// <summary>
    /// Gets a value indicating whether closing the main window hides it in the tray instead of quitting.
    /// </summary>
    bool CloseToTray { get; }

    /// <summary>
    /// Changes what closing the main window does, and saves the choice.
    /// </summary>
    /// <param name="value">The new value.</param>
    void SetCloseToTray(bool value);

    /// <summary>
    /// Gets a value indicating whether the main window stays hidden (tray only) when the application starts.
    /// </summary>
    bool StartMinimized { get; }

    /// <summary>
    /// Changes whether the application starts without showing the main window, and saves the choice.
    /// </summary>
    /// <param name="value">The new value.</param>
    void SetStartMinimized(bool value);

    /// <summary>
    /// Gets a value indicating whether the graphics card may draw the application; read when it starts.
    /// </summary>
    bool UseHardwareRendering { get; }

    /// <summary>
    /// Changes whether the graphics card is used from the next start on, and saves the choice.
    /// </summary>
    /// <param name="value">The new value.</param>
    void SetUseHardwareRendering(bool value);

    /// <summary>
    /// Gets a value indicating whether the web view showing mail is released while the main window is hidden.
    /// </summary>
    bool ReleaseWebViewWhenHidden { get; }

    /// <summary>
    /// Changes whether the web view is released while the main window is hidden, and saves the choice.
    /// </summary>
    /// <param name="value">The new value.</param>
    void SetReleaseWebViewWhenHidden(bool value);

    /// <summary>
    /// Gets a value indicating whether the user interface is animated.
    /// </summary>
    bool EnableAnimations { get; }

    /// <summary>
    /// Raised after a changed <see cref="EnableAnimations"/> was saved, so the animations can follow at once.
    /// </summary>
    event EventHandler? EnableAnimationsChanged;

    /// <summary>
    /// Changes whether the user interface is animated, and saves the choice.
    /// </summary>
    /// <param name="value">The new value.</param>
    void SetEnableAnimations(bool value);

    /// <summary>
    /// Gets a value indicating whether newly arrived mail is announced with a pop-up.
    /// </summary>
    bool ShowNewMailNotifications { get; }

    /// <summary>
    /// Changes whether newly arrived mail is announced, and saves the choice.
    /// </summary>
    /// <param name="value">The new value.</param>
    void SetShowNewMailNotifications(bool value);

    /// <summary>
    /// Gets how the tray icon points out unread mail, one of the <see cref="Settings.TrayUnreadStyle"/> values.
    /// </summary>
    string TrayUnreadStyle { get; }

    /// <summary>
    /// Raised after a changed <see cref="TrayUnreadStyle"/> was saved, so the tray icon can follow at once.
    /// </summary>
    event EventHandler? TrayUnreadStyleChanged;

    /// <summary>
    /// Changes how the tray icon points out unread mail, and saves the choice.
    /// </summary>
    /// <param name="value">One of the <see cref="Settings.TrayUnreadStyle"/> values.</param>
    void SetTrayUnreadStyle(string value);

    /// <summary>
    /// Gets the order in which contact names are shown, one of the <see cref="Settings.ContactNameOrder"/> values.
    /// </summary>
    string ContactNameOrder { get; }

    /// <summary>
    /// Raised after a changed <see cref="ContactNameOrder"/> was saved, so shown names can follow at once.
    /// </summary>
    event EventHandler? ContactNameOrderChanged;

    /// <summary>
    /// Changes the order of contact names, and saves the choice.
    /// </summary>
    /// <param name="value">One of the <see cref="Settings.ContactNameOrder"/> values.</param>
    void SetContactNameOrder(string value);

    /// <summary>
    /// Gets the saved theme preference, one of the <see cref="ThemePreference"/> values.
    /// </summary>
    string Theme { get; }

    /// <summary>
    /// Changes the theme preference and saves the choice.
    /// </summary>
    /// <param name="value">One of the <see cref="ThemePreference"/> values.</param>
    void SetTheme(string value);

    /// <summary>
    /// Gets where the main window was last, or <see langword="null"/> when nothing was saved.
    /// </summary>
    WindowPlacement? MainWindowPlacement { get; }

    /// <summary>
    /// Saves where the main window is.
    /// </summary>
    /// <param name="value">The placement.</param>
    void SetMainWindowPlacement(WindowPlacement value);

    /// <summary>
    /// Gets a value indicating whether new versions are looked for automatically.
    /// </summary>
    bool CheckForUpdates { get; }

    /// <summary>
    /// Changes whether new versions are looked for automatically, and saves the choice.
    /// </summary>
    /// <param name="value">The new value.</param>
    void SetCheckForUpdates(bool value);

    /// <summary>
    /// Gets what is remembered about earlier update checks.
    /// </summary>
    UpdateCheckState UpdateCheckState { get; }

    /// <summary>
    /// Changes what is remembered about update checks and saves it, as one step.
    /// </summary>
    /// <param name="change">Builds the new state from the current one.</param>
    void ChangeUpdateCheckState(Func<UpdateCheckState, UpdateCheckState> change);
}

/// <summary>
/// Stores the language preference and the user settings in one small JSON file.
/// </summary>
public sealed class AppSettings : ILanguagePreferenceStore, IAppSettings, IAiSettings
{
    /// <summary>
    /// How the AI settings are stored: the provider by name, so the file stays readable and reordering is harmless.
    /// </summary>
    private static readonly JsonSerializerOptions AiOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// The settings file path.
    /// </summary>
    private readonly string _path;

    /// <summary>
    /// Serializes read-modify-write cycles of the file.
    /// </summary>
    private readonly Lock _gate = new();

    /// <summary>
    /// The in-memory value, read by the sync worker while the UI thread writes it.
    /// </summary>
    private volatile bool _removeServerDeletedMessages;

    /// <summary>
    /// The in-memory value, read by the backfill worker while the UI thread writes it.
    /// </summary>
    private volatile bool _downloadFullHistory;

    /// <summary>
    /// The in-memory value, read when the main window is closed.
    /// </summary>
    private volatile bool _closeToTray;

    /// <summary>
    /// The in-memory value, read when new mail arrives.
    /// </summary>
    private volatile bool _showNewMailNotifications;

    /// <summary>
    /// The in-memory value, read when the application starts.
    /// </summary>
    private volatile bool _startMinimized;

    /// <summary>
    /// The in-memory value, read when the application starts.
    /// </summary>
    private volatile bool _useHardwareRendering;

    /// <summary>
    /// The in-memory value, read when the main window is hidden.
    /// </summary>
    private volatile bool _releaseWebViewWhenHidden;

    /// <summary>
    /// The in-memory value, read when the application starts and when it changes.
    /// </summary>
    private volatile bool _enableAnimations;

    /// <summary>
    /// The in-memory language preference, guarded by <see cref="_gate"/>; kept so a corrupt file never resets it.
    /// </summary>
    private string? _language;

    /// <summary>
    /// The in-memory theme preference.
    /// </summary>
    private volatile string _theme;

    /// <summary>
    /// The in-memory tray icon style.
    /// </summary>
    private volatile string _trayUnreadStyle;

    /// <summary>
    /// The in-memory contact name order.
    /// </summary>
    private volatile string _contactNameOrder;

    /// <summary>
    /// The in-memory main window placement.
    /// </summary>
    private volatile WindowPlacement? _mainWindowPlacement;

    /// <summary>
    /// The in-memory value, read before every automatic update check.
    /// </summary>
    private volatile bool _checkForUpdates;

    /// <summary>
    /// The in-memory state of the update checks, read from any thread.
    /// </summary>
    private volatile UpdateCheckState _updateCheckState;

    /// <summary>
    /// The in-memory AI settings.
    /// </summary>
    private volatile AiConfiguration _ai;

    /// <summary>
    /// Initializes a new instance of the <see cref="AppSettings"/> class and reads the file once.
    /// </summary>
    /// <param name="path">The settings file path.</param>
    public AppSettings(string path)
    {
        _path = path;
        var file = Read();
        _language = file.Language;
        _removeServerDeletedMessages = file.SyncServerDeletions ?? true;
        _downloadFullHistory = file.DownloadFullHistory ?? true;
        _closeToTray = file.CloseToTray ?? true;
        _showNewMailNotifications = file.ShowNewMailNotifications ?? true;
        _startMinimized = file.StartMinimized ?? false;
        _useHardwareRendering = file.UseHardwareRendering ?? true;
        _releaseWebViewWhenHidden = file.ReleaseWebViewWhenHidden ?? false;
        _enableAnimations = file.EnableAnimations ?? true;
        _theme = ThemePreference.Normalize(
            file.Theme is { ValueKind: JsonValueKind.String } theme ? theme.GetString() : null);
        _trayUnreadStyle = Settings.TrayUnreadStyle.Normalize(
            file.TrayUnreadStyle is { ValueKind: JsonValueKind.String } style ? style.GetString() : null);
        _contactNameOrder = Settings.ContactNameOrder.Normalize(
            file.ContactNameOrder is { ValueKind: JsonValueKind.String } order ? order.GetString() : null);
        _mainWindowPlacement = ReadPlacement(file.MainWindowPlacement);
        _checkForUpdates = file.CheckForUpdates ?? true;
        _updateCheckState = ReadUpdateCheckState(file.UpdateCheck);
        _ai = ReadAi(file.Ai);
    }

    /// <summary>
    /// The shape of the settings file; a missing member keeps its default so older files still load.
    /// </summary>
    /// <param name="Language">The saved language preference.</param>
    /// <param name="SyncServerDeletions">Whether messages deleted on the server are removed locally. Earlier versions stored this as "removeServerDeletedMessages" with an off default, so that key is deliberately not read.</param>
    /// <param name="DownloadFullHistory">Whether the full message history is downloaded in the background.</param>
    /// <param name="Theme">The theme preference; read as raw JSON so a wrongly typed value does not discard the whole file.</param>
    /// <param name="CloseToTray">Whether closing the main window hides it in the tray.</param>
    /// <param name="ShowNewMailNotifications">Whether newly arrived mail is announced with a pop-up.</param>
    /// <param name="TrayUnreadStyle">How the tray icon points out unread mail; raw JSON for the same reason as the theme.</param>
    /// <param name="MainWindowPlacement">Where the main window was; raw JSON for the same reason as the theme.</param>
    /// <param name="StartMinimized">Whether the main window stays hidden when the application starts.</param>
    /// <param name="ContactNameOrder">The order of contact names; raw JSON for the same reason as the theme.</param>
    /// <param name="CheckForUpdates">Whether new versions are looked for automatically.</param>
    /// <param name="UpdateCheck">What is remembered about update checks; raw JSON for the same reason as the theme.</param>
    /// <param name="Ai">The AI settings, without the API key; raw JSON for the same reason as the theme.</param>
    /// <param name="UseHardwareRendering">Whether the graphics card may draw the application.</param>
    /// <param name="ReleaseWebViewWhenHidden">Whether the web view is released while the main window is hidden.</param>
    /// <param name="EnableAnimations">Whether the user interface is animated.</param>
    private sealed record SettingsFile(
        string? Language,
        bool? SyncServerDeletions,
        bool? DownloadFullHistory = null,
        JsonElement? Theme = null,
        bool? CloseToTray = null,
        bool? ShowNewMailNotifications = null,
        JsonElement? TrayUnreadStyle = null,
        JsonElement? MainWindowPlacement = null,
        bool? StartMinimized = null,
        JsonElement? ContactNameOrder = null,
        bool? CheckForUpdates = null,
        JsonElement? UpdateCheck = null,
        JsonElement? Ai = null,
        bool? UseHardwareRendering = null,
        bool? ReleaseWebViewWhenHidden = null,
        bool? EnableAnimations = null);

    /// <inheritdoc />
    public bool RemoveServerDeletedMessages => _removeServerDeletedMessages;

    /// <inheritdoc />
    public bool DownloadFullHistory => _downloadFullHistory;

    /// <inheritdoc />
    public string Theme => _theme;

    /// <inheritdoc />
    public bool CloseToTray => _closeToTray;

    /// <inheritdoc />
    public bool ShowNewMailNotifications => _showNewMailNotifications;

    /// <inheritdoc />
    public bool StartMinimized => _startMinimized;

    /// <inheritdoc />
    public bool UseHardwareRendering => _useHardwareRendering;

    /// <inheritdoc />
    public bool ReleaseWebViewWhenHidden => _releaseWebViewWhenHidden;

    /// <inheritdoc />
    public bool EnableAnimations => _enableAnimations;

    /// <inheritdoc />
    public event EventHandler? EnableAnimationsChanged;

    /// <inheritdoc />
    public string TrayUnreadStyle => _trayUnreadStyle;

    /// <inheritdoc />
    public event EventHandler? TrayUnreadStyleChanged;

    /// <inheritdoc />
    public string ContactNameOrder => _contactNameOrder;

    /// <inheritdoc />
    public event EventHandler? ContactNameOrderChanged;

    /// <inheritdoc />
    public WindowPlacement? MainWindowPlacement => _mainWindowPlacement;

    /// <inheritdoc />
    public AiConfiguration Ai => _ai;

    /// <inheritdoc />
    public event EventHandler? AiChanged;

    /// <inheritdoc />
    public void SetAi(AiConfiguration value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            Write(Snapshot() with { Ai = JsonSerializer.SerializeToElement(value, AiOptions) });
            _ai = value;
        }

        // Outside the lock, so a listener may read the settings from any thread.
        AiChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void SetMainWindowPlacement(WindowPlacement value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            if (value == _mainWindowPlacement)
            {
                return;
            }

            Write(Snapshot() with { MainWindowPlacement = SerializePlacement(value) });
            _mainWindowPlacement = value;
        }
    }

    /// <inheritdoc />
    public bool CheckForUpdates => _checkForUpdates;

    /// <inheritdoc />
    public UpdateCheckState UpdateCheckState => _updateCheckState;

    /// <inheritdoc />
    public void SetCheckForUpdates(bool value)
    {
        lock (_gate)
        {
            Write(Snapshot() with { CheckForUpdates = value });
            _checkForUpdates = value;
        }
    }

    /// <inheritdoc />
    public void ChangeUpdateCheckState(Func<UpdateCheckState, UpdateCheckState> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            var state = change(_updateCheckState);
            if (state == _updateCheckState)
            {
                return;
            }

            Write(Snapshot() with { UpdateCheck = SerializeUpdateCheckState(state) });
            _updateCheckState = state;
        }
    }

    /// <inheritdoc />
    public string Load()
    {
        lock (_gate)
        {
            return Read().Language ?? LocalizationManager.System;
        }
    }

    /// <inheritdoc />
    public void Save(string preference)
    {
        lock (_gate)
        {
            Write(Snapshot() with { Language = preference });
            _language = preference;
        }
    }

    /// <inheritdoc />
    public void SetRemoveServerDeletedMessages(bool value)
    {
        lock (_gate)
        {
            Write(Snapshot() with { SyncServerDeletions = value });
            _removeServerDeletedMessages = value;
        }
    }

    /// <inheritdoc />
    public void SetDownloadFullHistory(bool value)
    {
        lock (_gate)
        {
            Write(Snapshot() with { DownloadFullHistory = value });
            _downloadFullHistory = value;
        }
    }

    /// <inheritdoc />
    public void SetCloseToTray(bool value)
    {
        lock (_gate)
        {
            Write(Snapshot() with { CloseToTray = value });
            _closeToTray = value;
        }
    }

    /// <inheritdoc />
    public void SetShowNewMailNotifications(bool value)
    {
        lock (_gate)
        {
            Write(Snapshot() with { ShowNewMailNotifications = value });
            _showNewMailNotifications = value;
        }
    }

    /// <inheritdoc />
    public void SetStartMinimized(bool value)
    {
        lock (_gate)
        {
            Write(Snapshot() with { StartMinimized = value });
            _startMinimized = value;
        }
    }

    /// <inheritdoc />
    public void SetUseHardwareRendering(bool value)
    {
        lock (_gate)
        {
            Write(Snapshot() with { UseHardwareRendering = value });
            _useHardwareRendering = value;
        }
    }

    /// <inheritdoc />
    public void SetReleaseWebViewWhenHidden(bool value)
    {
        lock (_gate)
        {
            Write(Snapshot() with { ReleaseWebViewWhenHidden = value });
            _releaseWebViewWhenHidden = value;
        }
    }

    /// <inheritdoc />
    public void SetEnableAnimations(bool value)
    {
        lock (_gate)
        {
            Write(Snapshot() with { EnableAnimations = value });
            _enableAnimations = value;
        }

        // Outside the lock, so a listener may read the settings from any thread.
        EnableAnimationsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void SetTrayUnreadStyle(string value)
    {
        var style = Settings.TrayUnreadStyle.Normalize(value);
        lock (_gate)
        {
            Write(Snapshot() with { TrayUnreadStyle = JsonSerializer.SerializeToElement(style) });
            _trayUnreadStyle = style;
        }

        // Outside the lock, so a listener may read the settings from any thread.
        TrayUnreadStyleChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void SetContactNameOrder(string value)
    {
        var order = Settings.ContactNameOrder.Normalize(value);
        lock (_gate)
        {
            Write(Snapshot() with { ContactNameOrder = JsonSerializer.SerializeToElement(order) });
            _contactNameOrder = order;
        }

        // Outside the lock, so a listener may read the settings from any thread.
        ContactNameOrderChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void SetTheme(string value)
    {
        var theme = ThemePreference.Normalize(value);
        lock (_gate)
        {
            Write(Snapshot() with { Theme = JsonSerializer.SerializeToElement(theme) });
            _theme = theme;
        }
    }

    /// <summary>
    /// Builds the file content from the values in memory; call it while holding <see cref="_gate"/>.
    /// </summary>
    /// <returns>The content.</returns>
    private SettingsFile Snapshot()
    {
        return new SettingsFile(
            _language,
            _removeServerDeletedMessages,
            _downloadFullHistory,
            JsonSerializer.SerializeToElement(_theme),
            _closeToTray,
            _showNewMailNotifications,
            JsonSerializer.SerializeToElement(_trayUnreadStyle),
            _mainWindowPlacement is { } placement ? SerializePlacement(placement) : null,
            _startMinimized,
            JsonSerializer.SerializeToElement(_contactNameOrder),
            _checkForUpdates,
            SerializeUpdateCheckState(_updateCheckState),
            JsonSerializer.SerializeToElement(_ai, AiOptions),
            _useHardwareRendering,
            _releaseWebViewWhenHidden,
            _enableAnimations);
    }

    /// <summary>
    /// Converts the state of the update checks to its stored form.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <returns>The JSON value.</returns>
    private static JsonElement SerializeUpdateCheckState(UpdateCheckState state)
    {
        return JsonSerializer.SerializeToElement(state, JsonSerializerOptions.Web);
    }

    /// <summary>
    /// Reads the stored state of the update checks.
    /// </summary>
    /// <param name="stored">The JSON value, if any.</param>
    /// <returns>The state, or an empty one when it is missing or unusable.</returns>
    private static UpdateCheckState ReadUpdateCheckState(JsonElement? stored)
    {
        if (stored is not { ValueKind: JsonValueKind.Object } element)
        {
            return new UpdateCheckState();
        }

        try
        {
            return element.Deserialize<UpdateCheckState>(JsonSerializerOptions.Web) ?? new UpdateCheckState();
        }
        catch (JsonException)
        {
            // A damaged value only means the next check is made as if there had been none.
            return new UpdateCheckState();
        }
    }

    /// <summary>
    /// Converts a window placement to its stored form.
    /// </summary>
    /// <param name="placement">The placement.</param>
    /// <returns>The JSON value.</returns>
    private static JsonElement SerializePlacement(WindowPlacement placement)
    {
        return JsonSerializer.SerializeToElement(placement, JsonSerializerOptions.Web);
    }

    /// <summary>
    /// Reads a stored window placement.
    /// </summary>
    /// <param name="stored">The JSON value, if any.</param>
    /// <returns>The placement, or <see langword="null"/> when it is missing or unusable.</returns>
    private static WindowPlacement? ReadPlacement(JsonElement? stored)
    {
        if (stored is not { ValueKind: JsonValueKind.Object } element)
        {
            return null;
        }

        try
        {
            return element.Deserialize<WindowPlacement>(JsonSerializerOptions.Web) is { HasUsableSize: true } placement
                ? placement
                : null;
        }
        catch (JsonException)
        {
            // A damaged placement only means the window opens at its default place.
            return null;
        }
    }

    /// <summary>
    /// Reads the stored AI settings.
    /// </summary>
    /// <param name="stored">The JSON value, if any.</param>
    /// <returns>The settings; the switched off defaults when they are missing or unusable.</returns>
    private static AiConfiguration ReadAi(JsonElement? stored)
    {
        if (stored is not { ValueKind: JsonValueKind.Object } element)
        {
            return AiConfiguration.Default;
        }

        try
        {
            var value = element.Deserialize<AiConfiguration>(AiOptions);
            if (value is not { Endpoint: not null, Model: not null } || !Enum.IsDefined(value.Provider))
            {
                return AiConfiguration.Default;
            }

            return value with
            {
                MaxInputCharacters = value.MaxInputCharacters <= 0
                    ? AiConfiguration.DefaultMaxInputCharacters
                    : Math.Clamp(
                        value.MaxInputCharacters,
                        AiConfiguration.MinInputCharacters,
                        AiConfiguration.MaxAllowedInputCharacters),
            };
        }
        catch (JsonException)
        {
            // Damaged AI settings only leave AI switched off.
            return AiConfiguration.Default;
        }
    }

    /// <summary>
    /// Reads the settings file.
    /// </summary>
    /// <returns>The file content, or all-default values when it is missing or unreadable.</returns>
    private SettingsFile Read()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new SettingsFile(null, null);
            }

            return JsonSerializer.Deserialize<SettingsFile>(File.ReadAllText(_path), JsonSerializerOptions.Web) ?? new SettingsFile(null, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A missing or corrupt settings file must never prevent the application from starting.
            return new SettingsFile(null, null);
        }
    }

    /// <summary>
    /// Writes the settings file, creating its directory first.
    /// </summary>
    /// <param name="file">The content to write.</param>
    private void Write(SettingsFile file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        File.WriteAllText(_path, JsonSerializer.Serialize(file, JsonSerializerOptions.Web));
    }
}
