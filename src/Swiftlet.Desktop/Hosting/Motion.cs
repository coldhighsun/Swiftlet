using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;
using Swiftlet.Desktop.Controls;

namespace Swiftlet.Desktop.Hosting;

/// <summary>
/// Switches the animations of the user interface on and off. Switched off, nothing in the application moves that would
/// not move without this class.
/// </summary>
public static class Motion
{
    /// <summary>
    /// The styles holding the color transitions and the looping animations.
    /// </summary>
    private static readonly Uri StylesSource = new("avares://Swiftlet/Styles/Motion.axaml");

    /// <summary>
    /// How long a color takes to change.
    /// </summary>
    private static readonly TimeSpan ColorDuration = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// The loaded styles while they are part of the application.
    /// </summary>
    private static IStyle? _styles;

    /// <summary>
    /// Gets the loaded animation styles, or <see langword="null"/> while the animations are switched off; for tests.
    /// </summary>
    internal static IStyle? Styles => _styles;

    /// <summary>
    /// Gets a value indicating whether the user interface is animated.
    /// </summary>
    public static bool IsEnabled { get; private set; }

    /// <summary>
    /// Gets the transitions of elements whose background changes; empty while the animations are switched off. The
    /// styles share these collections instead of owning their own, because removing a style does not take its values
    /// away from the parts inside a control template, while emptying a shared collection does.
    /// </summary>
    public static Transitions BackgroundTransitions { get; } = [];

    /// <summary>
    /// Gets the transitions of list rows, whose background and border change; empty while switched off.
    /// </summary>
    public static Transitions RowTransitions { get; } = [];

    /// <summary>
    /// Gets the transitions of text links, which replace the press effect the theme sets on every button and therefore
    /// repeat it; empty while switched off, when the links have no template part left to move.
    /// </summary>
    public static Transitions LinkTransitions { get; } = [];

    /// <summary>
    /// Adds or removes the animation styles and switches the animations started by code. Call it on the UI thread.
    /// </summary>
    /// <param name="application">The application whose styles change.</param>
    /// <param name="enabled">Whether the user interface is animated.</param>
    public static void Apply(Avalonia.Application application, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(application);
        IsEnabled = enabled;
        Fill(BackgroundTransitions, enabled, () => [Color(ContentPresenter.BackgroundProperty)]);
        Fill(
            RowTransitions,
            enabled,
            () => [Color(ContentPresenter.BackgroundProperty), Color(ContentPresenter.BorderBrushProperty)]);
        Fill(
            LinkTransitions,
            enabled,
            () =>
            [
                Color(Avalonia.Controls.Primitives.TemplatedControl.BackgroundProperty),
                new TransformOperationsTransition
                {
                    Property = Visual.RenderTransformProperty,
                    Duration = TimeSpan.FromMilliseconds(75),
                },
            ]);
        if (enabled && (_styles is null || !application.Styles.Contains(_styles)))
        {
            // Added as the loaded styles themselves: removing a StyleInclude would leave the values it applied behind.
            _styles = (IStyle)AvaloniaXamlLoader.Load(StylesSource);
            application.Styles.Add(_styles);
        }
        else if (!enabled && _styles is not null)
        {
            application.Styles.Remove(_styles);
            _styles = null;
        }
    }

    /// <summary>
    /// Builds the transition of a color. The property is shared by borders, presenters and controls of the same name.
    /// </summary>
    /// <param name="property">The brush property.</param>
    /// <returns>The transition.</returns>
    private static FillTransition Color(AvaloniaProperty property)
    {
        return new FillTransition { Property = property, Duration = ColorDuration };
    }

    /// <summary>
    /// Fills a shared collection while the animations are on and empties it otherwise.
    /// </summary>
    /// <param name="transitions">The collection.</param>
    /// <param name="enabled">Whether the user interface is animated.</param>
    /// <param name="create">Creates the content.</param>
    private static void Fill(Transitions transitions, bool enabled, Func<ITransition[]> create)
    {
        if (!enabled)
        {
            transitions.Clear();
        }
        else if (transitions.Count == 0)
        {
            transitions.AddRange(create());
        }
    }
}
