using System.Globalization;

namespace Swiftlet.Desktop.ViewModels;

/// <summary>
/// Formats the unread count shown in the folder tree.
/// </summary>
public static class UnreadBadge
{
    /// <summary>
    /// The largest number written out.
    /// </summary>
    private const int Largest = 999;

    /// <summary>
    /// Formats a count.
    /// </summary>
    /// <param name="count">The number of unread messages.</param>
    /// <returns>Nothing for zero, the number, or <c>999+</c>.</returns>
    public static string Format(int count)
    {
        if (count <= 0)
        {
            return string.Empty;
        }

        return count > Largest ? $"{Largest}+" : count.ToString(CultureInfo.InvariantCulture);
    }
}
