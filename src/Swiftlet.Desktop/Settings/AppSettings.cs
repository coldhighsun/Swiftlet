using System.Text.Json;
using Swiftlet.Application.Abstractions;
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
    /// Changes whether the "All Mail" folder is downloaded too, and saves the choice.
    /// </summary>
    /// <param name="value">The new value.</param>
    void SetDownloadAllMail(bool value);

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
    /// Gets the saved theme preference, one of the <see cref="ThemePreference"/> values.
    /// </summary>
    string Theme { get; }

    /// <summary>
    /// Changes the theme preference and saves the choice.
    /// </summary>
    /// <param name="value">One of the <see cref="ThemePreference"/> values.</param>
    void SetTheme(string value);
}

/// <summary>
/// Stores the language preference and the user settings in one small JSON file.
/// </summary>
public sealed class AppSettings : ILanguagePreferenceStore, IAppSettings
{
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
    /// The in-memory value, read by the backfill worker while the UI thread writes it.
    /// </summary>
    private volatile bool _downloadAllMail;

    /// <summary>
    /// The in-memory value, read when the main window is closed.
    /// </summary>
    private volatile bool _closeToTray;

    /// <summary>
    /// The in-memory language preference, guarded by <see cref="_gate"/>; kept so a corrupt file never resets it.
    /// </summary>
    private string? _language;

    /// <summary>
    /// The in-memory theme preference.
    /// </summary>
    private volatile string _theme;

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
        _downloadAllMail = file.DownloadAllMail ?? true;
        _closeToTray = file.CloseToTray ?? true;
        _theme = ThemePreference.Normalize(
            file.Theme is { ValueKind: JsonValueKind.String } theme ? theme.GetString() : null);
    }

    /// <summary>
    /// The shape of the settings file; a missing member keeps its default so older files still load.
    /// </summary>
    /// <param name="Language">The saved language preference.</param>
    /// <param name="SyncServerDeletions">Whether messages deleted on the server are removed locally. Earlier versions stored this as "removeServerDeletedMessages" with an off default, so that key is deliberately not read.</param>
    /// <param name="DownloadFullHistory">Whether the full message history is downloaded in the background.</param>
    /// <param name="DownloadAllMail">Whether the "All Mail" folder is downloaded too.</param>
    /// <param name="Theme">The theme preference; read as raw JSON so a wrongly typed value does not discard the whole file.</param>
    /// <param name="CloseToTray">Whether closing the main window hides it in the tray.</param>
    private sealed record SettingsFile(
        string? Language,
        bool? SyncServerDeletions,
        bool? DownloadFullHistory = null,
        bool? DownloadAllMail = null,
        JsonElement? Theme = null,
        bool? CloseToTray = null);

    /// <inheritdoc />
    public bool RemoveServerDeletedMessages => _removeServerDeletedMessages;

    /// <inheritdoc />
    public bool DownloadFullHistory => _downloadFullHistory;

    /// <inheritdoc />
    public bool DownloadAllMail => _downloadAllMail;

    /// <inheritdoc />
    public string Theme => _theme;

    /// <inheritdoc />
    public bool CloseToTray => _closeToTray;

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
    public void SetDownloadAllMail(bool value)
    {
        lock (_gate)
        {
            Write(Snapshot() with { DownloadAllMail = value });
            _downloadAllMail = value;
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
            _downloadAllMail,
            JsonSerializer.SerializeToElement(_theme),
            _closeToTray);
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
