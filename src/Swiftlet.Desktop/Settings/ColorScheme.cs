namespace Swiftlet.Desktop.Settings;

/// <summary>
/// The color schemes the user can save. Each one has colors for the light and the dark theme.
/// </summary>
public static class ColorScheme
{
    /// <summary>
    /// The default teal scheme.
    /// </summary>
    public const string Ocean = "ocean";

    /// <summary>
    /// A cool blue violet scheme.
    /// </summary>
    public const string Indigo = "indigo";

    /// <summary>
    /// A green scheme.
    /// </summary>
    public const string Forest = "forest";

    /// <summary>
    /// A warm scheme with a terracotta accent.
    /// </summary>
    public const string Sand = "sand";

    /// <summary>
    /// A neutral gray scheme.
    /// </summary>
    public const string Graphite = "graphite";

    /// <summary>
    /// Gets every scheme in the order the picker lists them.
    /// </summary>
    public static IReadOnlyList<string> All { get; } = [Ocean, Indigo, Forest, Sand, Graphite];

    /// <summary>
    /// Maps a stored value to a known scheme.
    /// </summary>
    /// <param name="value">The stored value.</param>
    /// <returns>The value when it is known, otherwise <see cref="Ocean"/>.</returns>
    public static string Normalize(string? value)
    {
        return value is Indigo or Forest or Sand or Graphite ? value : Ocean;
    }

    /// <summary>
    /// Gets the color that stands for a scheme in the picker: its accent in the light theme.
    /// </summary>
    /// <param name="scheme">One of the scheme values.</param>
    /// <returns>The color as <c>#RRGGBB</c>.</returns>
    public static string Swatch(string scheme)
    {
        return Normalize(scheme) switch
        {
            Indigo => "#4655B5",
            Forest => "#2E7D4F",
            Sand => "#A4541F",
            Graphite => "#3F4954",
            _ => "#0E7C9B",
        };
    }
}
