using AwesomeAssertions;

namespace Swiftlet.Application.Tests;

/// <summary>
/// Tests for the version of the application.
/// </summary>
public class AppVersionTests
{
    /// <summary>
    /// Verifies the build metadata is cut off a version.
    /// </summary>
    /// <param name="version">A semantic version.</param>
    /// <param name="expected">The version to show.</param>
    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3+0a1b2c3d", "1.2.3")]
    [InlineData("1.2.4-alpha.0.5+0a1b2c3d", "1.2.4-alpha.0.5")]
    [InlineData("", "")]
    public void WithoutMetadata_Version_DropsBuildMetadata(string version, string expected)
    {
        var result = AppVersion.WithoutMetadata(version);

        result.Should().Be(expected);
    }

    /// <summary>
    /// Verifies the build gave the assembly a semantic version.
    /// </summary>
    [Fact]
    public void Display_BuiltAssembly_IsSemanticVersion()
    {
        var version = AppVersion.Display;

        version.Should().MatchRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$");
        AppVersion.Full.Should().StartWith(version);
    }
}
