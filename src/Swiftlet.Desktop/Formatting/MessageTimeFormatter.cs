using System.Globalization;
using Swiftlet.Desktop.Localization;

namespace Swiftlet.Desktop.Formatting;

/// <summary>
/// Formats the time shown in the message list.
/// </summary>
public static class MessageTimeFormatter
{
    /// <summary>
    /// Formats a time relative to today: the time of day today, "yesterday", month and day this year, otherwise the date.
    /// </summary>
    /// <param name="sentAt">When the message was sent.</param>
    /// <param name="clock">Supplies the current time and the local time zone.</param>
    /// <param name="localizer">Supplies the word for yesterday.</param>
    /// <returns>The text.</returns>
    public static string Format(DateTimeOffset sentAt, TimeProvider clock, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(localizer);
        var local = TimeZoneInfo.ConvertTime(sentAt, clock.LocalTimeZone);
        var today = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), clock.LocalTimeZone).Date;
        if (local.Date == today)
        {
            return local.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        if (local.Date == today.AddDays(-1))
        {
            return localizer["Time_Yesterday"];
        }

        return local.ToString(local.Year == today.Year ? "MM-dd" : "yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
