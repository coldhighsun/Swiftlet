using Avalonia.Styling;
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
}
