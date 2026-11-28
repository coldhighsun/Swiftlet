using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Swiftlet.Desktop.Settings;

namespace Swiftlet.Desktop.Services;

/// <summary>
/// The Avalonia implementation of <see cref="IThemeApplier"/>.
/// </summary>
public sealed class AvaloniaThemeApplier : IThemeApplier
{
    /// <inheritdoc />
    public void Apply(string preference)
    {
        if (Avalonia.Application.Current is { } application)
        {
            application.RequestedThemeVariant = ThemePreference.Normalize(preference) switch
            {
                ThemePreference.Light => ThemeVariant.Light,
                ThemePreference.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }
    }

    /// <inheritdoc />
    public void ApplyColorScheme(string scheme)
    {
        if (Avalonia.Application.Current is not { } application)
        {
            return;
        }

        var dictionaries = application.Resources.MergedDictionaries;
        foreach (var overlay in dictionaries.OfType<SchemeDictionary>().ToList())
        {
            dictionaries.Remove(overlay);
        }

        if (DictionaryUri(scheme) is { } uri)
        {
            // Later dictionaries win, so the scheme hides the default colors of the same keys.
            var overlay = new SchemeDictionary();
            overlay.MergedDictionaries.Add((ResourceDictionary)AvaloniaXamlLoader.Load(uri));
            dictionaries.Add(overlay);
        }

        // The controls of the base theme (check boxes, pickers, selections) take their accent from its palettes.
        foreach (var theme in application.Styles.OfType<FluentTheme>())
        {
            foreach (var (variant, palette) in theme.Palettes)
            {
                if (application.TryGetResource("Brush.Accent", variant, out var value) && value is ISolidColorBrush accent)
                {
                    palette.Accent = accent.Color;
                }
            }
        }
    }

    /// <summary>
    /// Gets where the colors of a scheme are defined.
    /// </summary>
    /// <param name="scheme">One of the <see cref="ColorScheme"/> values.</param>
    /// <returns>The resource, or <see langword="null"/> for the default scheme, whose colors are always loaded.</returns>
    public static Uri? DictionaryUri(string scheme)
    {
        var name = ColorScheme.Normalize(scheme) switch
        {
            ColorScheme.Indigo => "Indigo",
            ColorScheme.Forest => "Forest",
            ColorScheme.Sand => "Sand",
            ColorScheme.Graphite => "Graphite",
            _ => null,
        };

        return name is null ? null : new Uri($"avares://Swiftlet/Styles/Schemes/{name}.axaml");
    }

    /// <summary>
    /// Holds the colors of the chosen scheme, so that they can be found and removed again.
    /// </summary>
    private sealed class SchemeDictionary : ResourceDictionary;
}
