using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Swiftlet.Desktop.Hosting;

namespace Swiftlet.Desktop.Controls;

/// <summary>
/// How a control comes into view.
/// </summary>
public enum EntranceEffect
{
    /// <summary>
    /// The control appears at once.
    /// </summary>
    None,

    /// <summary>
    /// The control fades in.
    /// </summary>
    Fade,

    /// <summary>
    /// The control fades in while moving down into its place.
    /// </summary>
    SlideDown,

    /// <summary>
    /// The control fades in while moving up into its place.
    /// </summary>
    SlideUp,

    /// <summary>
    /// The control fades in while growing to its size.
    /// </summary>
    Pop,
}

/// <summary>
/// Animates a control when it comes into view, while <see cref="Motion"/> is enabled. Only opacity and the render
/// transform change, so the layout is never affected, and nothing is left behind after the animation. Do not use it on a
/// control that contains the web view: a native control follows neither.
/// </summary>
public static class Entrance
{
    /// <summary>
    /// How long a control takes to come into view.
    /// </summary>
    internal static readonly TimeSpan EnterDuration = TimeSpan.FromMilliseconds(160);

    /// <summary>
    /// How long a closing pop-up takes to fade out.
    /// </summary>
    internal static readonly TimeSpan ExitDuration = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// Identifies the attached property that chooses the effect, which runs whenever the control becomes visible.
    /// </summary>
    public static readonly AttachedProperty<EntranceEffect> EffectProperty =
        AvaloniaProperty.RegisterAttached<Control, EntranceEffect>("Effect", typeof(Entrance));

    /// <summary>
    /// Identifies the attached property that also runs the effect when the control joins the visual tree, for content
    /// that is swapped in rather than shown.
    /// </summary>
    public static readonly AttachedProperty<bool> OnAttachProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("OnAttach", typeof(Entrance));

    /// <summary>
    /// Holds what cancels the animation a control is running.
    /// </summary>
    private static readonly AttachedProperty<CancellationTokenSource?> CancellationProperty =
        AvaloniaProperty.RegisterAttached<Control, CancellationTokenSource?>("Cancellation", typeof(Entrance));

    /// <summary>
    /// Holds the task of the last animation of a control.
    /// </summary>
    private static readonly AttachedProperty<Task?> RunningProperty =
        AvaloniaProperty.RegisterAttached<Control, Task?>("Running", typeof(Entrance));

    /// <summary>
    /// Watches the controls that get an effect.
    /// </summary>
    static Entrance()
    {
        EffectProperty.Changed.AddClassHandler<Control>((control, _) =>
        {
            control.PropertyChanged -= OnControlPropertyChanged;
            control.AttachedToVisualTree -= OnControlAttached;
            if (GetEffect(control) != EntranceEffect.None)
            {
                control.PropertyChanged += OnControlPropertyChanged;
                control.AttachedToVisualTree += OnControlAttached;
            }
        });
    }

    /// <summary>
    /// Gets the effect of a control.
    /// </summary>
    /// <param name="control">The control.</param>
    /// <returns>The effect.</returns>
    public static EntranceEffect GetEffect(Control control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return control.GetValue(EffectProperty);
    }

    /// <summary>
    /// Sets the effect of a control.
    /// </summary>
    /// <param name="control">The control.</param>
    /// <param name="value">The effect.</param>
    public static void SetEffect(Control control, EntranceEffect value)
    {
        ArgumentNullException.ThrowIfNull(control);
        control.SetValue(EffectProperty, value);
    }

    /// <summary>
    /// Gets whether the effect of a control also runs when it joins the visual tree.
    /// </summary>
    /// <param name="control">The control.</param>
    /// <returns>Whether it does.</returns>
    public static bool GetOnAttach(Control control)
    {
        ArgumentNullException.ThrowIfNull(control);
        return control.GetValue(OnAttachProperty);
    }

    /// <summary>
    /// Sets whether the effect of a control also runs when it joins the visual tree.
    /// </summary>
    /// <param name="control">The control.</param>
    /// <param name="value">Whether it does.</param>
    public static void SetOnAttach(Control control, bool value)
    {
        ArgumentNullException.ThrowIfNull(control);
        control.SetValue(OnAttachProperty, value);
    }

    /// <summary>
    /// Gets the task of the last animation of a control, for tests.
    /// </summary>
    /// <param name="control">The control.</param>
    /// <returns>The task, or <see langword="null"/> when the control was never animated.</returns>
    internal static Task? GetRunning(Control control)
    {
        return control.GetValue(RunningProperty);
    }

    /// <summary>
    /// Closes a pop-up window after fading out its content, or at once while animations are switched off. The content
    /// stops taking the pointer, so nothing can be clicked while it disappears.
    /// </summary>
    /// <param name="window">The window.</param>
    /// <param name="content">The visible content of the transparent window.</param>
    /// <returns>A task that completes when the window is closed.</returns>
    public static async Task CloseWithFadeAsync(Window window, Control content)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(content);
        if (Motion.IsEnabled && window.IsVisible)
        {
            content.IsHitTestVisible = false;
            Cancel(content);
            var fade = new Animation
            {
                Duration = ExitDuration,
                Easing = new CubicEaseIn(),

                // Stays invisible until the window is gone, instead of flashing up again.
                FillMode = FillMode.Forward,
                Children =
                {
                    Frame(0, new Setter(Visual.OpacityProperty, 1d)),
                    Frame(1, new Setter(Visual.OpacityProperty, 0d)),
                },
            };

            // The animation only advances while frames are drawn, so a limit makes sure the window always closes.
            try
            {
                await fade.RunAsync(content).WaitAsync(ExitDuration * 3);
            }
            catch (TimeoutException)
            {
                // No frames were drawn; the window closes without having faded.
            }
        }

        window.Close();
    }

    /// <summary>
    /// Runs the effect when a control becomes visible.
    /// </summary>
    /// <param name="sender">The control.</param>
    /// <param name="e">The changed property.</param>
    private static void OnControlPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // A control that is not on screen yet shows its first state without moving.
        if (e.Property == Visual.IsVisibleProperty && sender is Control control && control.IsVisible
            && control.IsAttachedToVisualTree())
        {
            Play(control);
        }
    }

    /// <summary>
    /// Runs the effect when a control that asks for it joins the visual tree.
    /// </summary>
    /// <param name="sender">The control.</param>
    /// <param name="e">The event data.</param>
    private static void OnControlAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control control && GetOnAttach(control) && control.IsVisible)
        {
            Play(control);
        }
    }

    /// <summary>
    /// Starts the effect of a control from the beginning.
    /// </summary>
    /// <param name="control">The control.</param>
    private static void Play(Control control)
    {
        if (!Motion.IsEnabled)
        {
            return;
        }

        Cancel(control);
        var cancellation = new CancellationTokenSource();
        control.SetValue(CancellationProperty, cancellation);
        control.SetValue(RunningProperty, Build(GetEffect(control)).RunAsync(control, cancellation.Token));
    }

    /// <summary>
    /// Stops the animation a control is running, if any.
    /// </summary>
    /// <param name="control">The control.</param>
    private static void Cancel(Control control)
    {
        if (control.GetValue(CancellationProperty) is not { } cancellation)
        {
            return;
        }

        control.SetValue(CancellationProperty, null);
        cancellation.Cancel();
        cancellation.Dispose();
    }

    /// <summary>
    /// Builds the animation of an effect.
    /// </summary>
    /// <param name="effect">The effect.</param>
    /// <returns>The animation.</returns>
    private static Animation Build(EntranceEffect effect)
    {
        List<Setter> from = [new Setter(Visual.OpacityProperty, 0d)];
        List<Setter> to = [new Setter(Visual.OpacityProperty, 1d)];
        switch (effect)
        {
            case EntranceEffect.SlideDown:
                from.Add(new Setter(TranslateTransform.YProperty, -8d));
                to.Add(new Setter(TranslateTransform.YProperty, 0d));
                break;
            case EntranceEffect.SlideUp:
                from.Add(new Setter(TranslateTransform.YProperty, 12d));
                to.Add(new Setter(TranslateTransform.YProperty, 0d));
                break;
            case EntranceEffect.Pop:
                from.Add(new Setter(ScaleTransform.ScaleXProperty, 0.6));
                from.Add(new Setter(ScaleTransform.ScaleYProperty, 0.6));
                to.Add(new Setter(ScaleTransform.ScaleXProperty, 1d));
                to.Add(new Setter(ScaleTransform.ScaleYProperty, 1d));
                break;
        }

        return new Animation
        {
            Duration = EnterDuration,
            Easing = new CubicEaseOut(),
            Children = { Frame(0, [.. from]), Frame(1, [.. to]) },
        };
    }

    /// <summary>
    /// Builds a key frame.
    /// </summary>
    /// <param name="cue">The position in the animation, from 0 to 1.</param>
    /// <param name="setters">The values at that position.</param>
    /// <returns>The key frame.</returns>
    private static KeyFrame Frame(double cue, params Setter[] setters)
    {
        var frame = new KeyFrame { Cue = new Cue(cue) };
        foreach (var setter in setters)
        {
            frame.Setters.Add(setter);
        }

        return frame;
    }
}
