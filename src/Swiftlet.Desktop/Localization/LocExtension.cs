using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace Swiftlet.Desktop.Localization;

/// <summary>
/// XAML markup extension binding a property to a localized string, for example <c>Content="{loc:Loc Toolbar_Compose}"</c>.
/// The value refreshes automatically when the language changes.
/// </summary>
/// <param name="key">The resource key.</param>
public sealed class LocExtension(string key) : MarkupExtension
{
    /// <summary>
    /// Gets the resource key.
    /// </summary>
    public string Key { get; } = key;

    /// <inheritdoc />
    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        return new ReflectionBinding($"[{Key}]") { Mode = BindingMode.OneWay, Source = LocalizationManager.Instance };
    }
}
