using Avalonia;
using AwesomeAssertions;
using Swiftlet.Desktop.Hosting;
using Swiftlet.Desktop.Settings;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests for fitting a saved window placement to the screens.
/// </summary>
public class WindowPlacementTests
{
    /// <summary>
    /// The smallest window size used by the tests.
    /// </summary>
    private static readonly Size Minimum = new(800, 480);

    /// <summary>
    /// A primary screen and a scaled one to its left.
    /// </summary>
    private static readonly (PixelRect WorkingArea, double Scaling)[] Screens =
    [
        (new PixelRect(0, 0, 1920, 1040), 1.0),
        (new PixelRect(-2560, 0, 2560, 1400), 1.5),
    ];

    /// <summary>
    /// Verifies a window on one of the screens, also partly outside it, stays where it was.
    /// </summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    [Theory]
    [InlineData(100, 50)]
    [InlineData(-2000, 100)]
    [InlineData(-7, 0)]
    [InlineData(1700, 900)]
    public void Fit_ReachableWindow_KeepsPlacement(int x, int y)
    {
        var saved = new WindowPlacement(x, y, 900, 600, false);

        RememberWindowPlacement.Fit(saved, Screens, Minimum).Should().Be(saved);
    }

    /// <summary>
    /// Verifies a window whose title bar is on no screen is centered on the first one.
    /// </summary>
    /// <param name="x">The left edge.</param>
    /// <param name="y">The top edge.</param>
    [Theory]
    [InlineData(4000, 100)]
    [InlineData(100, -300)]
    [InlineData(1880, 100)]
    [InlineData(100, 1035)]
    public void Fit_UnreachableWindow_CentersOnFirstScreen(int x, int y)
    {
        var saved = new WindowPlacement(x, y, 900, 600, true);

        var fitted = RememberWindowPlacement.Fit(saved, Screens, Minimum);

        fitted.Should().Be(new WindowPlacement(510, 220, 900, 600, true));
    }

    /// <summary>
    /// Verifies a centered window is not larger than the screen.
    /// </summary>
    [Fact]
    public void Fit_UnreachableLargeWindow_ShrinksToScreen()
    {
        var saved = new WindowPlacement(9000, 9000, 3000, 2000, false);

        var fitted = RememberWindowPlacement.Fit(saved, Screens, Minimum);

        fitted.Should().Be(new WindowPlacement(0, 0, 1920, 1040, false));
    }

    /// <summary>
    /// Verifies a size below the minimum of the window is raised to it.
    /// </summary>
    [Fact]
    public void Fit_TooSmall_UsesMinimumSize()
    {
        var saved = new WindowPlacement(10, 10, 200, 100, false);

        var fitted = RememberWindowPlacement.Fit(saved, Screens, Minimum);

        fitted.Should().Be(saved with { Width = 800, Height = 480 });
    }

    /// <summary>
    /// Verifies the position is kept when the window system reports no screen.
    /// </summary>
    [Fact]
    public void Fit_NoScreens_KeepsPosition()
    {
        var saved = new WindowPlacement(4000, 100, 900, 600, false);

        RememberWindowPlacement.Fit(saved, [], Minimum).Should().Be(saved);
    }
}
