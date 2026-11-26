using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Swiftlet.Desktop.Converters;

/// <summary>
/// Turns the resource key of an icon (chosen by a view model) into its geometry.
/// </summary>
public sealed class IconKeyConverter : IValueConverter
{
    /// <summary>
    /// Gets the shared instance used from XAML.
    /// </summary>
    public static IconKeyConverter Instance { get; } = new();

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is string key
            && Avalonia.Application.Current is { } application
            && application.TryGetResource(key, application.ActualThemeVariant, out var resource)
                ? resource as Geometry
                : null;
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
