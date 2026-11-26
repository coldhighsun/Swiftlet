using CommunityToolkit.Mvvm.ComponentModel;

namespace Swiftlet.Desktop.ViewModels.Settings;

/// <summary>
/// A selectable theme.
/// </summary>
/// <param name="code">The preference value, for example <c>dark</c>.</param>
/// <param name="displayName">Produces the display name in the current language.</param>
public sealed class ThemeOptionViewModel(string code, Func<string> displayName) : ObservableObject
{
    /// <summary>
    /// Gets the preference value represented by this option.
    /// </summary>
    public string Code { get; } = code;

    /// <summary>
    /// Gets the name shown in the theme picker.
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
