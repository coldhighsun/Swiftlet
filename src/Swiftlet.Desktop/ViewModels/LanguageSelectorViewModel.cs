using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Swiftlet.Desktop.Localization;

namespace Swiftlet.Desktop.ViewModels;

/// <summary>
/// A selectable UI language.
/// </summary>
/// <param name="code">The preference value, for example <c>zh-Hans</c>.</param>
/// <param name="displayName">Produces the display name in the current language.</param>
public sealed class LanguageOptionViewModel(string code, Func<string> displayName) : ObservableObject
{
    /// <summary>
    /// Gets the preference value represented by this option.
    /// </summary>
    public string Code { get; } = code;

    /// <summary>
    /// Gets the name shown in the language picker.
    /// </summary>
    public string DisplayName => displayName();

    /// <summary>
    /// Re-reads the display name after the UI language changed.
    /// </summary>
    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(DisplayName));
    }
}

/// <summary>
/// The view model of the language picker: applies the chosen language immediately and remembers it.
/// </summary>
public sealed partial class LanguageSelectorViewModel : ObservableObject
{
    /// <summary>
    /// The localizer whose language is changed.
    /// </summary>
    private readonly ILocalizer _localizer;

    /// <summary>
    /// The store that persists the preference.
    /// </summary>
    private readonly ILanguagePreferenceStore _store;

    /// <summary>
    /// The logger.
    /// </summary>
    private readonly ILogger<LanguageSelectorViewModel> _logger;

    /// <summary>
    /// Whether the initial selection is being assigned, which must not be applied or saved again.
    /// </summary>
    private bool _initializing;

    /// <summary>
    /// Initializes a new instance of the <see cref="LanguageSelectorViewModel"/> class.
    /// </summary>
    /// <param name="localizer">The localizer.</param>
    /// <param name="store">The preference store.</param>
    /// <param name="logger">The logger.</param>
    public LanguageSelectorViewModel(ILocalizer localizer, ILanguagePreferenceStore store, ILogger<LanguageSelectorViewModel> logger)
    {
        _localizer = localizer;
        _store = store;
        _logger = logger;
        Options =
        [
            new LanguageOptionViewModel(LocalizationManager.System, () => localizer["Language_System"]),
            new LanguageOptionViewModel(LocalizationManager.SimplifiedChinese, () => localizer["Language_ChineseSimplified"]),
            new LanguageOptionViewModel(LocalizationManager.English, () => localizer["Language_English"]),
        ];

        _initializing = true;
        SelectedOption = Options.First(o => o.Code == localizer.Preference);
        _initializing = false;
        localizer.LanguageChanged += (_, _) =>
        {
            foreach (var option in Options)
            {
                option.RefreshLanguage();
            }
        };
    }

    /// <summary>
    /// Gets the available languages.
    /// </summary>
    public IReadOnlyList<LanguageOptionViewModel> Options { get; }

    /// <summary>
    /// Gets or sets the selected language.
    /// </summary>
    [ObservableProperty]
    public partial LanguageOptionViewModel SelectedOption { get; set; }

    /// <summary>
    /// Applies and saves the newly selected language.
    /// </summary>
    /// <param name="value">The new selection.</param>
    partial void OnSelectedOptionChanged(LanguageOptionViewModel value)
    {
        if (_initializing || value is null)
        {
            return;
        }

        _localizer.SetPreference(value.Code);
        try
        {
            _store.Save(value.Code);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The language still applies for this session; only remembering it failed.
            _logger.LogWarning(ex, "Could not save the language preference.");
        }
    }
}
