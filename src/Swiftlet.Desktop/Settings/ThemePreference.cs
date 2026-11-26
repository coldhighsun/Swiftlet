namespace Swiftlet.Desktop.Settings;

/// <summary>
/// The theme choices the user can save.
/// </summary>
public static class ThemePreference
{
    /// <summary>
    /// Follow the theme of the operating system.
    /// </summary>
    public const string System = "system";

    /// <summary>
    /// Always light.
    /// </summary>
    public const string Light = "light";

    /// <summary>
    /// Always dark.
    /// </summary>
    public const string Dark = "dark";

    /// <summary>
    /// Maps a stored value to a known preference.
    /// </summary>
    /// <param name="value">The stored value.</param>
    /// <returns>The value when it is known, otherwise <see cref="System"/>.</returns>
    public static string Normalize(string? value)
    {
        return value is Light or Dark ? value : System;
    }
}
