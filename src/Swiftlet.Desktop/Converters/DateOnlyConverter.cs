using System.Globalization;
using Avalonia.Data.Converters;

namespace Swiftlet.Desktop.Converters;

/// <summary>
/// Binds a <see cref="DateOnly"/> property to a date picker, whose selected date is a <see cref="DateTime"/>.
/// </summary>
public sealed class DateOnlyConverter : IValueConverter
{
    /// <summary>
    /// Gets the shared instance.
    /// </summary>
    public static DateOnlyConverter Instance { get; } = new();

    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is DateOnly day ? day.ToDateTime(TimeOnly.MinValue) : null;
    }

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is DateTime date ? DateOnly.FromDateTime(date) : null;
    }
}
