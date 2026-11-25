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
    }

    /// <summary>
    /// The shape of the settings file; a missing member keeps its default so older files still load.
    /// </summary>
    /// <param name="Language">The saved language preference.</param>
    /// <param name="RemoveServerDeletedMessages">Whether messages deleted on the server are removed locally.</param>
    private sealed record SettingsFile(string? Language, bool? RemoveServerDeletedMessages);

    /// <inheritdoc />
    public bool RemoveServerDeletedMessages => _removeServerDeletedMessages;

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
            Write(new SettingsFile(preference, _removeServerDeletedMessages));
            _language = preference;
        }
    }

    /// <inheritdoc />
    public void SetRemoveServerDeletedMessages(bool value)
    {
        lock (_gate)
        {
            Write(new SettingsFile(_language, value));
            _removeServerDeletedMessages = value;
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
