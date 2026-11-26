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
    /// The in-memory language preference, guarded by <see cref="_gate"/>; kept so a corrupt file never resets it.
    /// </summary>
    private string? _language;

    /// <summary>
    /// Initializes a new instance of the <see cref="AppSettings"/> class and reads the file once.
    /// </summary>
    /// <param name="path">The settings file path.</param>
    public AppSettings(string path)
    {
        _path = path;
        var file = Read();
        _language = file.Language;
        _removeServerDeletedMessages = file.RemoveServerDeletedMessages ?? false;
        _downloadFullHistory = file.DownloadFullHistory ?? true;
        _downloadAllMail = file.DownloadAllMail ?? true;
    }

    /// <summary>
    /// The shape of the settings file; a missing member keeps its default so older files still load.
    /// </summary>
    /// <param name="Language">The saved language preference.</param>
    /// <param name="RemoveServerDeletedMessages">Whether messages deleted on the server are removed locally.</param>
    /// <param name="DownloadFullHistory">Whether the full message history is downloaded in the background.</param>
    /// <param name="DownloadAllMail">Whether the "All Mail" folder is downloaded too.</param>
    private sealed record SettingsFile(
        string? Language,
        bool? RemoveServerDeletedMessages,
        bool? DownloadFullHistory = null,
        bool? DownloadAllMail = null);

    /// <inheritdoc />
    public bool RemoveServerDeletedMessages => _removeServerDeletedMessages;

    /// <inheritdoc />
    public bool DownloadFullHistory => _downloadFullHistory;

    /// <inheritdoc />
    public bool DownloadAllMail => _downloadAllMail;

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
            Write(new SettingsFile(preference, _removeServerDeletedMessages, _downloadFullHistory, _downloadAllMail));
            _language = preference;
        }
    }

    /// <inheritdoc />
    public void SetRemoveServerDeletedMessages(bool value)
    {
        lock (_gate)
        {
            Write(new SettingsFile(_language, value, _downloadFullHistory, _downloadAllMail));
            _removeServerDeletedMessages = value;
        }
    }

    /// <inheritdoc />
    public void SetDownloadFullHistory(bool value)
    {
        lock (_gate)
        {
            Write(new SettingsFile(_language, _removeServerDeletedMessages, value, _downloadAllMail));
            _downloadFullHistory = value;
        }
    }

    /// <inheritdoc />
    public void SetDownloadAllMail(bool value)
    {
        lock (_gate)
        {
            Write(new SettingsFile(_language, _removeServerDeletedMessages, _downloadFullHistory, value));
            _downloadAllMail = value;
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
