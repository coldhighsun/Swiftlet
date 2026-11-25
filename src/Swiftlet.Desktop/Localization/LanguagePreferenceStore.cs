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
