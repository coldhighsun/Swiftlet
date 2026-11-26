using System.Globalization;

namespace Swiftlet.Desktop.ViewModels;

/// <summary>
/// Derives the round initial shown for a sender or an account.
/// </summary>
public static class Avatar
{
    /// <summary>
    /// Gets the background colors; every one keeps white text readable.
    /// </summary>
    public static IReadOnlyList<string> Palette { get; } =
        ["#0E7C9B", "#2F6FB0", "#6A55B5", "#A8478A", "#B5483C", "#9A6400", "#3F7D3A", "#546A7B"];

    /// <summary>
    /// Gets the first character of the name, or of the address when there is no name.
    /// </summary>
    /// <param name="name">The display name.</param>
    /// <param name="address">The email address.</param>
    /// <returns>One whole character (surrogate pairs included) in upper case, or <c>?</c>.</returns>
    public static string Initial(string? name, string? address)
    {
        var text = string.IsNullOrWhiteSpace(name) ? address?.Trim() : name.Trim();
        return string.IsNullOrEmpty(text) ? "?" : StringInfo.GetNextTextElement(text).ToUpperInvariant();
    }

    /// <summary>
    /// Picks the color of an address; the same address always gets the same color, in every run.
    /// </summary>
    /// <param name="address">The email address.</param>
    /// <returns>The color as <c>#RRGGBB</c>.</returns>
    public static string Color(string? address)
    {
        // FNV-1a: string.GetHashCode differs between runs.
        var hash = 2166136261u;
        foreach (var character in (address ?? string.Empty).Trim().ToLowerInvariant())
        {
            hash = unchecked((hash ^ character) * 16777619u);
        }

        return Palette[(int)(hash % (uint)Palette.Count)];
    }
}
