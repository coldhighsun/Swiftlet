using Swiftlet.Desktop.Localization;

namespace Swiftlet.Desktop.Formatting;

/// <summary>
/// Describes how long mail stays in Trash.
/// </summary>
public static class TrashRetentionText
{
    /// <summary>
    /// Formats a retention period.
    /// </summary>
    /// <param name="localizer">The localizer.</param>
    /// <param name="days">The number of days.</param>
    /// <returns>The text.</returns>
    public static string Format(ILocalizer localizer, int days)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return localizer.Format("TrashRetention_Days", days);
    }
}
