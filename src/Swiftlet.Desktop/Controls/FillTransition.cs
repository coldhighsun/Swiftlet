using Avalonia.Animation;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Swiftlet.Desktop.Controls;

/// <summary>
/// Changes a brush gradually, mixing colors by how much of them is seen. The brush transition of the framework mixes the
/// color channels on their own, so a change from "transparent", which is a white nobody sees, to a dark color passes
/// through a pale one and flashes in the dark theme.
/// </summary>
public sealed class FillTransition : InterpolatingTransitionBase<IBrush?>
{
    /// <inheritdoc />
    protected override IBrush? Interpolate(double progress, IBrush? from, IBrush? to)
    {
        // Only plain colors can be mixed; anything else changes at the end.
        if (from is not (null or ISolidColorBrush) || to is not (null or ISolidColorBrush))
        {
            return progress < 1 ? from : to;
        }

        if (progress >= 1)
        {
            return to;
        }

        return new ImmutableSolidColorBrush(Mix(ColorOf(from), ColorOf(to), Math.Max(progress, 0)));
    }

    /// <summary>
    /// Mixes two colors, weighting each color by its alpha.
    /// </summary>
    /// <param name="from">The color at the start.</param>
    /// <param name="to">The color at the end.</param>
    /// <param name="progress">How far the change is, from 0 to 1.</param>
    /// <returns>The mixed color.</returns>
    private static Color Mix(Color from, Color to, double progress)
    {
        var fromWeight = from.A * (1 - progress);
        var toWeight = to.A * progress;
        var alpha = fromWeight + toWeight;
        if (alpha <= 0)
        {
            return Color.FromArgb(0, to.R, to.G, to.B);
        }

        return Color.FromArgb(
            (byte)Math.Round(alpha),
            (byte)Math.Round(((from.R * fromWeight) + (to.R * toWeight)) / alpha),
            (byte)Math.Round(((from.G * fromWeight) + (to.G * toWeight)) / alpha),
            (byte)Math.Round(((from.B * fromWeight) + (to.B * toWeight)) / alpha));
    }

    /// <summary>
    /// Gets the color a brush paints, its opacity included; no brush paints nothing.
    /// </summary>
    /// <param name="brush">A plain color brush, or <see langword="null"/>.</param>
    /// <returns>The color.</returns>
    private static Color ColorOf(IBrush? brush)
    {
        if (brush is not ISolidColorBrush solid)
        {
            return default;
        }

        var color = solid.Color;
        return Color.FromArgb((byte)Math.Round(color.A * Math.Clamp(solid.Opacity, 0, 1)), color.R, color.G, color.B);
    }
}
