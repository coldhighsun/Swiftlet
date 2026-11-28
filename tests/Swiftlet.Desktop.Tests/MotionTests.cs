using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AwesomeAssertions;
using Swiftlet.Desktop.Controls;
using Swiftlet.Desktop.Hosting;
using Swiftlet.Desktop.Views;
using Xunit;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Runs the animation tests alone: the switch is global, and every other UI test expects a user interface that does not
/// move.
/// </summary>
[CollectionDefinition(nameof(MotionTests), DisableParallelization = true)]
public class MotionTestGroup;

/// <summary>
/// Tests for the animations of the user interface. They run on the shared headless session and switch the animations off
/// again.
/// </summary>
[Collection(nameof(MotionTests))]
public class MotionTests
{
    /// <summary>
    /// Runs a test body on the UI thread and switches the animations off afterwards.
    /// </summary>
    /// <param name="body">The test body.</param>
    /// <returns>A task that completes when the body has completed.</returns>
    private static Task<bool> DispatchAsync(Func<Task> body)
    {
        return UiSmokeTests.Session.Dispatch(
            async () =>
            {
                try
                {
                    await body();
                }
                finally
                {
                    Motion.Apply(Avalonia.Application.Current!, false);
                }

                return true;
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Draws frames until a task has completed, failing after a while.
    /// </summary>
    /// <param name="task">The task.</param>
    /// <returns>A task that completes with the given one.</returns>
    private static async Task DrawUntilAsync(Task task)
    {
        for (var attempt = 0; attempt < 200 && !task.IsCompleted; attempt++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
        }

        task.IsCompleted.Should().BeTrue();
        await task;
    }

    /// <summary>
    /// Verifies switching the animations adds the styles once and removes them, with the transitions of buttons.
    /// </summary>
    [Fact]
    public async Task Apply_OnAndOff_AddsAndRemovesTransitions()
    {
        await DispatchAsync(() =>
        {
            var button = new Button { Content = "x" };
            var panel = new StackPanel { Children = { button } };
            var window = new Window { Content = panel };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var presenter = button.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");

            Motion.Apply(Avalonia.Application.Current!, true);
            Motion.Apply(Avalonia.Application.Current!, true);
            Dispatcher.UIThread.RunJobs();

            Motion.IsEnabled.Should().BeTrue();
            var styles = Motion.Styles;
            Avalonia.Application.Current!.Styles.Count(s => s == styles).Should().Be(1);
            presenter.Transitions.Should().NotBeNullOrEmpty();
            var link = new HyperlinkButton { Classes = { "link" } };
            panel.Children.Add(link);
            Dispatcher.UIThread.RunJobs();
            link.Transitions.Should().HaveCount(2);

            Motion.Apply(Avalonia.Application.Current!, false);
            Dispatcher.UIThread.RunJobs();

            Motion.IsEnabled.Should().BeFalse();
            Motion.Styles.Should().BeNull();
            Avalonia.Application.Current!.Styles.Should().NotContain(styles!);
            presenter.Transitions.Should().BeNullOrEmpty();

            // Switching on again reaches the button that kept the shared collection.
            Motion.Apply(Avalonia.Application.Current!, true);
            Dispatcher.UIThread.RunJobs();
            presenter.Transitions.Should().NotBeNullOrEmpty();
            window.Close();
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Verifies a change from transparent shows only the target color, more and more of it, instead of passing through
    /// the white that "transparent" is made of.
    /// </summary>
    [Fact]
    public void FillTransition_FromTransparent_KeepsTheTargetColor()
    {
        var target = new ImmutableSolidColorBrush(Color.FromRgb(20, 40, 80));

        var halfway = (ISolidColorBrush)InspectableFill.At(0.5, Brushes.Transparent, target)!;
        var fromNothing = (ISolidColorBrush)InspectableFill.At(0.5, null, target)!;

        halfway.Color.Should().Be(Color.FromArgb(128, 20, 40, 80));
        fromNothing.Color.Should().Be(halfway.Color);
        InspectableFill.At(1, Brushes.Transparent, target).Should().BeSameAs(target);
    }

    /// <summary>
    /// Verifies two solid colors are mixed evenly.
    /// </summary>
    [Fact]
    public void FillTransition_BetweenSolidColors_MixesThem()
    {
        var halfway = (ISolidColorBrush)InspectableFill.At(
            0.5, new ImmutableSolidColorBrush(Colors.Black), new ImmutableSolidColorBrush(Color.FromRgb(200, 100, 50)))!;

        halfway.Color.Should().Be(Color.FromRgb(100, 50, 25));
    }

    /// <summary>
    /// Verifies showing a control moves nothing while the animations are off.
    /// </summary>
    [Fact]
    public async Task Effect_AnimationsOff_DoesNotAnimate()
    {
        await DispatchAsync(() =>
        {
            var panel = new Border { IsVisible = false };
            Entrance.SetEffect(panel, EntranceEffect.Fade);
            var window = new Window { Content = panel };
            window.Show();

            panel.IsVisible = true;

            Entrance.GetRunning(panel).Should().BeNull();
            panel.Opacity.Should().Be(1);
            window.Close();
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Verifies a shown control is animated and ends up fully visible and in its place.
    /// </summary>
    /// <param name="effect">The effect.</param>
    [Theory]
    [InlineData(EntranceEffect.Fade)]
    [InlineData(EntranceEffect.SlideDown)]
    [InlineData(EntranceEffect.SlideUp)]
    [InlineData(EntranceEffect.Pop)]
    public async Task Effect_ControlShown_AnimatesAndLeavesNothingBehind(EntranceEffect effect)
    {
        await DispatchAsync(async () =>
        {
            Motion.Apply(Avalonia.Application.Current!, true);
            var panel = new Border { Width = 40, Height = 40, IsVisible = false };
            Entrance.SetEffect(panel, effect);
            var window = new Window { Content = panel };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            panel.IsVisible = true;
            var running = Entrance.GetRunning(panel);

            running.Should().NotBeNull();
            await DrawUntilAsync(running!);
            panel.Opacity.Should().Be(1);
            var matrix = panel.RenderTransform?.Value ?? Avalonia.Matrix.Identity;
            matrix.Should().Be(Avalonia.Matrix.Identity);
            window.Close();
        });
    }

    /// <summary>
    /// Verifies only a control that asks for it is animated when it joins a window.
    /// </summary>
    /// <param name="onAttach">Whether the control asks for it.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Effect_ControlAttached_AnimatesOnlyOnRequest(bool onAttach)
    {
        await DispatchAsync(() =>
        {
            Motion.Apply(Avalonia.Application.Current!, true);
            var panel = new Border();
            Entrance.SetEffect(panel, EntranceEffect.Fade);
            Entrance.SetOnAttach(panel, onAttach);
            var window = new Window { Content = panel };

            window.Show();
            Dispatcher.UIThread.RunJobs();

            (Entrance.GetRunning(panel) is not null).Should().Be(onAttach);
            window.Close();
            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Verifies a pop-up closes at once while the animations are off.
    /// </summary>
    [Fact]
    public async Task CloseWithFadeAsync_AnimationsOff_ClosesAtOnce()
    {
        await DispatchAsync(async () =>
        {
            var content = new Border();
            var window = new Window { Content = content };
            window.Show();

            var closing = Entrance.CloseWithFadeAsync(window, content);

            closing.IsCompleted.Should().BeTrue();
            window.IsVisible.Should().BeFalse();
            await closing;
        });
    }

    /// <summary>
    /// Verifies a pop-up stays, without taking the pointer, until it has faded out.
    /// </summary>
    [Fact]
    public async Task CloseWithFadeAsync_AnimationsOn_ClosesAfterTheFade()
    {
        await DispatchAsync(async () =>
        {
            Motion.Apply(Avalonia.Application.Current!, true);
            var content = new Border();
            var window = new Window { Content = content };
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var closing = Entrance.CloseWithFadeAsync(window, content);

            window.IsVisible.Should().BeTrue();
            content.IsHitTestVisible.Should().BeFalse();
            await DrawUntilAsync(closing);
            window.IsVisible.Should().BeFalse();
        });
    }

    /// <summary>
    /// Verifies the windows that use the effects can be shown with the animations on.
    /// </summary>
    [Fact]
    public async Task Windows_AnimationsOn_CanBeShown()
    {
        await DispatchAsync(() =>
        {
            Motion.Apply(Avalonia.Application.Current!, true);
            Window[] windows =
            [
                new MainWindow(), new ComposeWindow(), new SettingsWindow(), new ContactsWindow(), new NewMailWindow(),
                new AttachmentPreviewWindow(), new UpdateNoticeWindow(), new SplashWindow(),
            ];

            foreach (var window in windows)
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                window.Close();
            }

            return Task.CompletedTask;
        });
    }

    /// <summary>
    /// Gives the tests the value a <see cref="FillTransition"/> has at a point of the change.
    /// </summary>
    private static class InspectableFill
    {
        /// <summary>
        /// The method of the transition, which only the framework calls.
        /// </summary>
        private static readonly System.Reflection.MethodInfo Method = typeof(FillTransition).GetMethod(
            "Interpolate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        /// <summary>
        /// Gets the brush at a point of the change.
        /// </summary>
        /// <param name="progress">How far the change is.</param>
        /// <param name="from">The brush at the start.</param>
        /// <param name="to">The brush at the end.</param>
        /// <returns>The brush.</returns>
        public static IBrush? At(double progress, IBrush? from, IBrush? to)
        {
            return (IBrush?)Method.Invoke(new FillTransition(), [progress, from, to]);
        }
    }
}
