using Swiftlet.Desktop.Localization;

namespace Swiftlet.Desktop.Formatting;

/// <summary>
/// Describes a synchronization interval.
/// </summary>
public static class SyncIntervalText
{
    /// <summary>
    /// Formats an interval, such as "Every 5 minutes".
    /// </summary>
    /// <param name="localizer">The localizer.</param>
    /// <param name="interval">The interval.</param>
    /// <returns>The text.</returns>
    public static string Format(ILocalizer localizer, TimeSpan interval)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return interval == TimeSpan.FromMinutes(1)
            ? localizer["EditAccount_IntervalOneMinute"]
            : localizer.Format("EditAccount_IntervalMinutes", interval.TotalMinutes);
    }
}
