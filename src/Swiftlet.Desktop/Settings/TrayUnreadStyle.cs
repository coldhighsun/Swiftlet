namespace Swiftlet.Desktop.Settings;

/// <summary>
/// How the tray icon can point out unread mail.
/// </summary>
public static class TrayUnreadStyle
{
    /// <summary>
    /// A red dot in the corner of the icon.
    /// </summary>
    public const string Dot = "dot";

    /// <summary>
    /// The icon alternates between its normal form and the one with the red dot.
    /// </summary>
    public const string Blink = "blink";

    /// <summary>
    /// Maps a stored value to a known style.
    /// </summary>
    /// <param name="value">The stored value.</param>
    /// <returns>The value when it is known, otherwise <see cref="Dot"/>.</returns>
    public static string Normalize(string? value)
    {
        return value is Blink ? value : Dot;
    }
}
