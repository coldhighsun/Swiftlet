namespace Swiftlet.Desktop.Services;

/// <summary>
/// Changes the theme of the running application.
/// </summary>
public interface IThemeApplier
{
    /// <summary>
    /// Applies a theme preference at once.
    /// </summary>
    /// <param name="preference">One of the <see cref="Settings.ThemePreference"/> values.</param>
    void Apply(string preference);
}
