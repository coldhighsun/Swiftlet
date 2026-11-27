using AwesomeAssertions;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Swiftlet.Desktop.Views;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests for the design tokens and the shared control styles.
/// </summary>
public class ThemeTests
{
    /// <summary>
    /// The smallest contrast ratio WCAG AA accepts for body text.
    /// </summary>
    private const double MinimumContrast = 4.5;

    /// <summary>
    /// Every color token the windows may refer to.
    /// </summary>
    private static readonly string[] Tokens =
    [
        "Brush.Accent", "Brush.AccentHover", "Brush.AccentText", "Brush.OnAccent", "Brush.AccentSubtle", "Brush.WindowBg", "Brush.SidebarBg",
        "Brush.Surface", "Brush.Border", "Brush.TextPrimary", "Brush.TextSecondary", "Brush.Danger", "Brush.Star",
        "Brush.WarningBg",
    ];

    /// <summary>
    /// Every icon the windows may refer to.
    /// </summary>
    private static readonly string[] IconNames =
    [
        "Compose", "Reply", "Forward", "Star", "StarFilled", "Tag", "Browser", "Delete", "Sync", "Search", "Settings",
        "AddAccount", "Attach", "Download", "Close", "Inbox", "AllInboxes", "Sent", "Drafts", "Trash", "Junk", "Archive",
        "AllMail", "Folder", "Mail", "Shield", "Info", "Logo",
    ];

    /// <summary>
    /// Verifies both theme variants define every token.
    /// </summary>
    [Fact]
    public async Task Colors_EveryToken_IsDefinedForBothThemes()
    {
        await UiSmokeTests.Session.Dispatch(
            () =>
            {
                foreach (var variant in (ThemeVariant[])[ThemeVariant.Light, ThemeVariant.Dark])
                {
                    foreach (var token in Tokens)
                    {
                        Avalonia.Application.Current!.TryGetResource(token, variant, out var value)
                            .Should().BeTrue($"{token} must exist in {variant}");
                        value.Should().BeAssignableTo<ISolidColorBrush>($"{token} in {variant}");
                    }
                }
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies every pair of text and background used by the design stays readable in both themes.
    /// </summary>
    [Fact]
    public async Task Colors_TextOnBackgrounds_MeetContrast()
    {
        await UiSmokeTests.Session.Dispatch(
            () =>
            {
                string[] texts = ["Brush.TextPrimary", "Brush.TextSecondary", "Brush.AccentText"];
                string[] backgrounds = ["Brush.WindowBg", "Brush.SidebarBg", "Brush.Surface", "Brush.AccentSubtle"];
                var pairs = texts.SelectMany(t => backgrounds.Select(b => (Text: t, Background: b))).ToList();
                pairs.Add(("Brush.OnAccent", "Brush.Accent"));
                pairs.Add(("Brush.OnAccent", "Brush.AccentHover"));
                pairs.Add(("Brush.Danger", "Brush.Surface"));
                pairs.Add(("Brush.TextPrimary", "Brush.WarningBg"));

                foreach (var variant in (ThemeVariant[])[ThemeVariant.Light, ThemeVariant.Dark])
                {
                    foreach (var (text, background) in pairs)
                    {
                        Contrast(Color(text, variant), Color(background, variant))
                            .Should().BeGreaterThanOrEqualTo(MinimumContrast, $"{text} on {background} in {variant}");
                    }
                }
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the brand accent of both themes.
    /// </summary>
    [Fact]
    public async Task Colors_Accent_IsTheBrandColor()
    {
        await UiSmokeTests.Session.Dispatch(
            () =>
            {
                Color("Brush.Accent", ThemeVariant.Light).Should().Be(Avalonia.Media.Color.Parse("#0E7C9B"));
                Color("Brush.Accent", ThemeVariant.Dark).Should().Be(Avalonia.Media.Color.Parse("#4FB8D4"));
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies windows get the base font size and background without asking for them.
    /// </summary>
    [Fact]
    public async Task Controls_Window_UsesBrandDefaults()
    {
        await UiSmokeTests.Session.Dispatch(
            () =>
            {
                var window = new StartupWindow();
                window.Show();
                Dispatcher.UIThread.RunJobs();

                window.FontSize.Should().Be(14);
                window.Background.Should().BeAssignableTo<ISolidColorBrush>().Which.Color
                    .Should().Be(Color("Brush.WindowBg", window.ActualThemeVariant));
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the primary button uses the readable text color of the accent, which is dark in the dark theme.
    /// </summary>
    [Fact]
    public async Task Controls_AccentButton_UsesOnAccentText()
    {
        await UiSmokeTests.Session.Dispatch(
            () =>
            {
                var button = new Button { Content = "Send", Classes = { "accent" } };
                var window = new Window { Content = button, RequestedThemeVariant = ThemeVariant.Dark };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                var presenter = button.GetVisualDescendants().OfType<ContentPresenter>().First();
                presenter.Foreground.Should().BeAssignableTo<ISolidColorBrush>().Which.Color
                    .Should().Be(Color("Brush.OnAccent", ThemeVariant.Dark));
                presenter.Background.Should().BeAssignableTo<ISolidColorBrush>().Which.Color
                    .Should().Be(Color("Brush.Accent", ThemeVariant.Dark));
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies every icon the windows may refer to is a drawable geometry.
    /// </summary>
    [Fact]
    public async Task Icons_EveryKey_ResolvesToGeometry()
    {
        await UiSmokeTests.Session.Dispatch(
            () =>
            {
                foreach (var name in IconNames)
                {
                    Avalonia.Application.Current!.TryGetResource($"Icon.{name}", ThemeVariant.Light, out var value)
                        .Should().BeTrue($"Icon.{name} must exist");
                    value.Should().BeAssignableTo<Geometry>().Which.Bounds.Width.Should().BeGreaterThan(0, name);
                }
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the application icon is shipped as an asset and every window uses it.
    /// </summary>
    [Fact]
    public async Task AppIcon_Asset_IsUsedByWindows()
    {
        await UiSmokeTests.Session.Dispatch(
            () =>
            {
                using var stream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://Swiftlet.Desktop/Assets/swiftlet.ico"));
                new WindowIcon(stream).Should().NotBeNull();
                foreach (var window in (Window[])[new MainWindow(), new ComposeWindow(), new AddAccountWindow(), new SettingsWindow(), new LabelManagerWindow(), new StartupWindow()])
                {
                    window.Icon.Should().NotBeNull(window.GetType().Name);
                }
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies a disabled primary or destructive button does not look like one that can be pressed.
    /// </summary>
    /// <param name="style">The style class of the button.</param>
    /// <param name="dark">Whether the dark theme is used.</param>
    [Theory]
    [InlineData("accent", false)]
    [InlineData("accent", true)]
    [InlineData("danger", false)]
    [InlineData("danger", true)]
    public async Task Controls_DisabledButton_LooksDisabled(string style, bool dark)
    {
        await UiSmokeTests.Session.Dispatch(
            () =>
            {
                var enabled = new Button { Content = "Send", Classes = { style } };
                var disabled = new Button { Content = "Send", Classes = { style }, IsEnabled = false };
                var variant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                var window = new Window
                {
                    Content = new StackPanel { Children = { enabled, disabled } },
                    RequestedThemeVariant = variant,
                };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                Avalonia.Application.Current!.TryGetResource("ButtonForegroundDisabled", variant, out var expected).Should().BeTrue();
                var presenter = disabled.GetVisualDescendants().OfType<ContentPresenter>().First();
                var usual = enabled.GetVisualDescendants().OfType<ContentPresenter>().First();
                var text = presenter.Foreground.Should().BeAssignableTo<ISolidColorBrush>().Subject.Color;
                text.Should().Be(((ISolidColorBrush)expected!).Color);
                text.Should().NotBe(((ISolidColorBrush)usual.Foreground!).Color);
                if (style == "accent")
                {
                    ((ISolidColorBrush)presenter.Background!).Color.Should().NotBe(Color("Brush.Accent", variant));
                }

                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies check boxes stay square: the rounder control radius would make them look like radio buttons.
    /// </summary>
    [Fact]
    public async Task Controls_CheckBox_KeepsSquareCorners()
    {
        await UiSmokeTests.Session.Dispatch(
            () =>
            {
                var box = new CheckBox { Content = "x" };
                var window = new Window { Content = box };
                window.Show();
                Dispatcher.UIThread.RunJobs();

                box.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "NormalRectangle")
                    .CornerRadius.Should().Be(new Avalonia.CornerRadius(4));
                window.Close();
            },
            TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies the windows take colors from the tokens instead of fixed colors or faded text.
    /// </summary>
    [Fact]
    public void Xaml_Windows_UseTokensForColors()
    {
        var directory = Path.Combine(Path.GetDirectoryName(SourceFile())!, "..", "..", "src", "Swiftlet.Desktop", "Views");
        var windows = Directory.GetFiles(Path.GetFullPath(directory), "*.axaml", SearchOption.AllDirectories);

        windows.Should().HaveCountGreaterThanOrEqualTo(6);
        foreach (var file in windows)
        {
            var text = File.ReadAllText(file);
            text.Should().NotContain("IndianRed", Path.GetFileName(file));
            text.Should().NotContain("Opacity=", Path.GetFileName(file));
            text.Should().NotContain("SystemFillColor", Path.GetFileName(file));
        }
    }

    /// <summary>
    /// Gets the path of this source file.
    /// </summary>
    /// <param name="path">Supplied by the compiler.</param>
    /// <returns>The path.</returns>
    private static string SourceFile([System.Runtime.CompilerServices.CallerFilePath] string path = "")
    {
        return path;
    }

    /// <summary>
    /// Reads the color of a token.
    /// </summary>
    /// <param name="token">The resource key.</param>
    /// <param name="variant">The theme variant.</param>
    /// <returns>The color.</returns>
    private static Color Color(string token, ThemeVariant variant)
    {
        Avalonia.Application.Current!.TryGetResource(token, variant, out var value).Should().BeTrue($"{token} must exist");
        return ((ISolidColorBrush)value!).Color;
    }

    /// <summary>
    /// Computes the WCAG contrast ratio of two opaque colors.
    /// </summary>
    /// <param name="first">One color.</param>
    /// <param name="second">The other color.</param>
    /// <returns>A ratio from 1 to 21.</returns>
    internal static double Contrast(Color first, Color second)
    {
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    /// <summary>
    /// Computes the WCAG relative luminance of a color.
    /// </summary>
    /// <param name="color">The color.</param>
    /// <returns>The luminance from 0 to 1.</returns>
    private static double Luminance(Color color)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
    }
}
