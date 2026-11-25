using System.ComponentModel;
using System.Globalization;
using System.Resources;

namespace Swiftlet.Desktop.Localization;

/// <summary>
/// Resource-based <see cref="ILocalizer"/> that also notifies XAML bindings when the language changes.
/// </summary>
public sealed class LocalizationManager : ILocalizer, INotifyPropertyChanged
{
    /// <summary>
    /// The language code of Simplified Chinese.
    /// </summary>
    public const string SimplifiedChinese = "zh-Hans";

    /// <summary>
    /// The language code of English, which is also the neutral resource language.
    /// </summary>
    public const string English = "en";

    /// <summary>
    /// The preference value meaning "use the operating system language".
    /// </summary>
    public const string System = "system";

    /// <summary>
    /// The resource manager over the embedded <c>Strings</c> resources.
    /// </summary>
    private static readonly ResourceManager Resources = new("Swiftlet.Desktop.Resources.Strings", typeof(LocalizationManager).Assembly);

    /// <summary>
    /// The operating system UI culture.
    /// </summary>
    private readonly CultureInfo _systemCulture;

    /// <summary>
    /// The culture strings are currently resolved against.
    /// </summary>
    private CultureInfo _culture;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalizationManager"/> class.
    /// </summary>
    /// <param name="systemCulture">The operating system UI culture.</param>
    /// <param name="preference">The initial preference.</param>
    public LocalizationManager(CultureInfo systemCulture, string preference = System)
    {
        ArgumentNullException.ThrowIfNull(systemCulture);
        _systemCulture = systemCulture;
        Preference = Normalize(preference);
        _culture = CultureFor(Preference, systemCulture);
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <inheritdoc />
    public event EventHandler? LanguageChanged;

    /// <summary>
    /// Gets the instance used by XAML markup extensions, following the operating system language until told otherwise.
    /// </summary>
    public static LocalizationManager Instance { get; } = new(CultureInfo.CurrentUICulture);

    /// <inheritdoc />
    public string Language => _culture.Name;

    /// <inheritdoc />
    public string Preference { get; private set; }

    /// <inheritdoc />
    public string this[string key]
    {
        get
        {
            ArgumentNullException.ThrowIfNull(key);
            return Resources.GetString(key, _culture) ?? $"!{key}!";
        }
    }

    /// <summary>
    /// Maps any culture to one of the supported languages: every Chinese culture to Simplified Chinese, everything else to English.
    /// </summary>
    /// <param name="culture">The culture to map.</param>
    /// <returns>The supported culture.</returns>
    public static CultureInfo Resolve(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        return string.Equals(culture.TwoLetterISOLanguageName, "zh", StringComparison.OrdinalIgnoreCase)
            ? CultureInfo.GetCultureInfo(SimplifiedChinese)
            : CultureInfo.GetCultureInfo(English);
    }

    /// <inheritdoc />
    public string Format(string key, params object?[] args)
    {
        return string.Format(_culture, this[key], args);
    }

    /// <inheritdoc />
    public void SetPreference(string preference)
    {
        Preference = Normalize(preference);
        var culture = CultureFor(Preference, _systemCulture);
        if (culture.Name == _culture.Name)
        {
            return;
        }

        _culture = culture;

        // "Item[]" tells Avalonia that every indexer binding of this object must be re-evaluated.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Normalizes a preference string to one of the known values.
    /// </summary>
    /// <param name="preference">The raw preference.</param>
    /// <returns>The normalized preference.</returns>
    private static string Normalize(string? preference)
    {
        if (string.Equals(preference, SimplifiedChinese, StringComparison.OrdinalIgnoreCase))
        {
            return SimplifiedChinese;
        }

        return string.Equals(preference, English, StringComparison.OrdinalIgnoreCase) ? English : System;
    }

    /// <summary>
    /// Gets the culture for a normalized preference.
    /// </summary>
    /// <param name="preference">The normalized preference.</param>
    /// <param name="systemCulture">The operating system UI culture.</param>
    /// <returns>The culture to use.</returns>
    private static CultureInfo CultureFor(string preference, CultureInfo systemCulture)
    {
        return preference == System ? Resolve(systemCulture) : CultureInfo.GetCultureInfo(preference);
    }
}
