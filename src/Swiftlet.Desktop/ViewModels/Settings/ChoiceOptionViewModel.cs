using CommunityToolkit.Mvvm.ComponentModel;

namespace Swiftlet.Desktop.ViewModels.Settings;

/// <summary>
/// One entry of a picker in the settings window.
/// </summary>
/// <param name="code">The preference value, for example <c>dark</c>.</param>
/// <param name="displayName">Produces the display name in the current language.</param>
public sealed class ChoiceOptionViewModel(string code, Func<string> displayName) : ObservableObject
{
    /// <summary>
    /// Gets the preference value represented by this option.
    /// </summary>
    public string Code { get; } = code;

    /// <summary>
    /// Gets the name shown in the picker.
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
