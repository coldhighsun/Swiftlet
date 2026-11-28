using CommunityToolkit.Mvvm.ComponentModel;
using Swiftlet.Desktop.Localization;

namespace Swiftlet.Desktop.ViewModels.Settings;

/// <summary>
/// One saved rule that allows remote images, as listed in the settings window.
/// </summary>
/// <param name="rule">The saved rule: a sender address or a sender domain.</param>
/// <param name="localizer">The localizer.</param>
public sealed class RemoteImageRuleViewModel(string rule, ILocalizer localizer) : ObservableObject
{
    /// <summary>
    /// Gets the saved rule.
    /// </summary>
    public string Rule { get; } = rule;

    /// <summary>
    /// Gets a value indicating whether the rule covers a domain and its subdomains rather than one address.
    /// </summary>
    public bool IsDomain => !Rule.Contains('@', StringComparison.Ordinal);

    /// <summary>
    /// Gets the localized name of the kind of rule.
    /// </summary>
    public string KindText => IsDomain
        ? localizer["Settings_RemoteImagesKindDomain"]
        : localizer["Settings_RemoteImagesKindAddress"];

    /// <summary>
    /// Re-reads the localized texts after the UI language changed.
    /// </summary>
    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(KindText));
    }
}
