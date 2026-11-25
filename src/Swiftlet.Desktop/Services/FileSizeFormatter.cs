using System.Globalization;

namespace Swiftlet.Desktop.Services;

/// <summary>
/// Formats byte counts for display.
/// </summary>
public static class FileSizeFormatter
{
    /// <summary>
    /// The unit names from bytes upwards.
    /// </summary>
    private static readonly string[] Units = ["B", "KB", "MB", "GB"];

    /// <summary>
    /// Formats a size using the largest unit that keeps the value at or above one.
    /// </summary>
    /// <param name="bytes">The size in bytes.</param>
    /// <returns>The formatted size, for example <c>1.5 MB</c>.</returns>
    public static string Format(long bytes)
    {
        var value = (double)Math.Max(bytes, 0);
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{value:0} {Units[unit]}")
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {Units[unit]}");
    }
}
