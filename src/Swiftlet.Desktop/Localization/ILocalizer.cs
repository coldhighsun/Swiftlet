namespace Swiftlet.Desktop.Localization;

/// <summary>
/// Looks up localized UI strings and switches the UI language at runtime.
/// </summary>
public interface ILocalizer
{
    /// <summary>
    /// Raised after the effective UI language changed.
    /// </summary>
    event EventHandler? LanguageChanged;

    /// <summary>
    /// Gets the code of the effective language, <c>en</c> or <c>zh-Hans</c>.
    /// </summary>
    string Language { get; }

    /// <summary>
    /// Gets the user's preference: <c>system</c>, <c>en</c> or <c>zh-Hans</c>.
    /// </summary>
    string Preference { get; }

    /// <summary>
    /// Gets a localized string.
    /// </summary>
    /// <param name="key">The resource key.</param>
    /// <returns>The localized text, or <c>!key!</c> if the key does not exist.</returns>
    string this[string key] { get; }

    /// <summary>
    /// Gets a localized string and formats it with the given arguments.
    /// </summary>
    /// <param name="key">The resource key of the format string.</param>
    /// <param name="args">The format arguments.</param>
    /// <returns>The formatted text.</returns>
    string Format(string key, params object?[] args);

    /// <summary>
    /// Applies a language preference.
    /// </summary>
    /// <param name="preference"><c>system</c>, <c>en</c> or <c>zh-Hans</c>; anything else is treated as <c>system</c>.</param>
    void SetPreference(string preference);
}
