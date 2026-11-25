using System.Text.Json;

namespace Swiftlet.Desktop.Localization;

/// <summary>
/// Persists the user's language preference.
/// </summary>
public interface ILanguagePreferenceStore
{
    /// <summary>
    /// Loads the saved preference.
    /// </summary>
    /// <returns>The preference, or <c>system</c> if none was saved or it cannot be read.</returns>
    string Load();

    /// <summary>
    /// Saves the preference.
    /// </summary>
    /// <param name="preference">The preference to save.</param>
    void Save(string preference);
}

/// <summary>
/// Stores the language preference in a small JSON settings file.
/// </summary>
/// <param name="path">The settings file path.</param>
public sealed class JsonLanguagePreferenceStore(string path) : ILanguagePreferenceStore
{
    /// <summary>
    /// The shape of the settings file.
    /// </summary>
    /// <param name="Language">The saved preference.</param>
    private sealed record SettingsFile(string? Language);

    /// <inheritdoc />
    public string Load()
    {
        try
        {
            if (!File.Exists(path))
            {
                return LocalizationManager.System;
            }

            var settings = JsonSerializer.Deserialize<SettingsFile>(File.ReadAllText(path), JsonSerializerOptions.Web);
            return settings?.Language ?? LocalizationManager.System;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A missing or corrupt settings file must never prevent the application from starting.
            return LocalizationManager.System;
        }
    }

    /// <inheritdoc />
    public void Save(string preference)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(new SettingsFile(preference), JsonSerializerOptions.Web));
    }
}
