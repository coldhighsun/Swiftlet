using System.Text.RegularExpressions;

namespace Swiftlet.Domain.Labels;

/// <summary>
/// A user-defined label that can be attached to messages.
/// </summary>
public sealed partial class Label
{
    /// <summary>
    /// The maximum length of <see cref="Name"/>.
    /// </summary>
    public const int MaxNameLength = 50;

    /// <summary>
    /// Gets the unique identifier of the label.
    /// </summary>
    public Guid Id { get; private set; } = Guid.NewGuid();

    /// <summary>
    /// Gets the label name.
    /// </summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the label color as a <c>#RRGGBB</c> string.
    /// </summary>
    public string Color { get; private set; } = string.Empty;

    /// <summary>
    /// Initializes a new instance of the <see cref="Label"/> class for persistence.
    /// </summary>
    private Label()
    {
    }

    /// <summary>
    /// Creates a new label.
    /// </summary>
    /// <param name="name">The label name.</param>
    /// <param name="color">The color as <c>#RRGGBB</c>.</param>
    /// <returns>The created label.</returns>
    public static Label Create(string name, string color)
    {
        return new Label
        {
            Name = NormalizeName(name),
            Color = ValidateColor(color),
        };
    }

    /// <summary>
    /// Renames the label.
    /// </summary>
    /// <param name="name">The new name.</param>
    public void Rename(string name)
    {
        Name = NormalizeName(name);
    }

    /// <summary>
    /// Changes the label color.
    /// </summary>
    /// <param name="color">The new color as <c>#RRGGBB</c>.</param>
    public void SetColor(string color)
    {
        Color = ValidateColor(color);
    }

    /// <summary>
    /// Matches a <c>#RRGGBB</c> color.
    /// </summary>
    /// <returns>The regular expression.</returns>
    [GeneratedRegex(@"^#[0-9A-Fa-f]{6}\z", RegexOptions.None, matchTimeoutMilliseconds: 2000)]
    private static partial Regex ColorPattern();

    /// <summary>
    /// Trims and validates a label name.
    /// </summary>
    /// <param name="name">The raw name.</param>
    /// <returns>The trimmed name.</returns>
    private static string NormalizeName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmed = name.Trim();
        if (trimmed.Length > MaxNameLength)
        {
            throw new ArgumentException($"Label name must be at most {MaxNameLength} characters.", nameof(name));
        }

        return trimmed;
    }

    /// <summary>
    /// Validates a label color.
    /// </summary>
    /// <param name="color">The raw color.</param>
    /// <returns>The validated color.</returns>
    private static string ValidateColor(string color)
    {
        if (color is null || !ColorPattern().IsMatch(color))
        {
            throw new ArgumentException("Color must be in #RRGGBB format.", nameof(color));
        }

        return color;
    }
}
