using AwesomeAssertions;
using Swiftlet.Desktop.Hosting;
using Xunit;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests the web view command line that follows the hardware rendering choice.
/// </summary>
public sealed class HardwareRenderingTests
{
    /// <summary>
    /// Verifies nothing is changed while hardware rendering is on.
    /// </summary>
    /// <param name="current">The arguments already requested.</param>
    [Theory]
    [InlineData(null)]
    [InlineData("--lang=en")]
    public void BrowserArguments_Enabled_KeepsCurrent(string? current)
    {
        HardwareRendering.BrowserArguments(current, enabled: true).Should().Be(current);
    }

    /// <summary>
    /// Verifies the graphics card switch is added, keeping arguments already requested.
    /// </summary>
    /// <param name="current">The arguments already requested.</param>
    /// <param name="expected">The expected command line.</param>
    [Theory]
    [InlineData(null, "--disable-gpu")]
    [InlineData("", "--disable-gpu")]
    [InlineData("--lang=en", "--lang=en --disable-gpu")]
    public void BrowserArguments_Disabled_AddsDisableGpu(string? current, string expected)
    {
        HardwareRendering.BrowserArguments(current, enabled: false).Should().Be(expected);
    }
}
