namespace Swiftlet.Domain.Accounts;

/// <summary>
/// The colors that tell accounts apart. Every one keeps white text readable.
/// </summary>
public static class AccountColors
{
    /// <summary>
    /// The colors that can be derived from an address. Changing them, or their order, recolors every account and person
    /// without a stored color.
    /// </summary>
    private static readonly string[] DerivedColors =
        ["#0E7C9B", "#2F6FB0", "#6A55B5", "#A8478A", "#B5483C", "#9A6400", "#3F7D3A", "#546A7B"];

    /// <summary>
    /// Gets the colors an address can be given by <see cref="Derive"/>.
    /// </summary>
    public static IReadOnlyList<string> Derivable => DerivedColors;

    /// <summary>
    /// Gets the colors offered for accounts, as <c>#RRGGBB</c>.
    /// </summary>
    public static IReadOnlyList<string> Palette { get; } = [.. DerivedColors, "#C2185B", "#BF4B00", "#6B6F00", "#5E63D6"];

    /// <summary>
    /// Picks the color of an address; the same address always gets the same color, in every run.
    /// </summary>
    /// <param name="address">The email address.</param>
    /// <returns>The color as <c>#RRGGBB</c>.</returns>
    public static string Derive(string? address)
    {
        // FNV-1a: string.GetHashCode differs between runs.
        var hash = 2166136261u;
        foreach (var character in (address ?? string.Empty).Trim().ToLowerInvariant())
        {
            hash = unchecked((hash ^ character) * 16777619u);
        }

        return DerivedColors[(int)(hash % (uint)DerivedColors.Length)];
    }

    /// <summary>
    /// Picks a random color of the palette that is not in use; once all are in use, one of those used least.
    /// </summary>
    /// <param name="used">The colors in use, one entry per use.</param>
    /// <param name="random">The random source.</param>
    /// <returns>The color as <c>#RRGGBB</c>.</returns>
    public static string PickUnused(IEnumerable<string> used, Random random)
    {
        ArgumentNullException.ThrowIfNull(used);
        ArgumentNullException.ThrowIfNull(random);

        var uses = used.ToList();
        var counts = Palette
            .Select(color => (Color: color, Count: uses.Count(u => string.Equals(u, color, StringComparison.OrdinalIgnoreCase))))
            .ToList();
        var least = counts.Min(c => c.Count);
        var candidates = counts.Where(c => c.Count == least).Select(c => c.Color).ToList();
        return candidates[random.Next(candidates.Count)];
    }
}
