using AwesomeAssertions;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests the name of the published program.
/// </summary>
public class AssemblyNameTests
{
    /// <summary>
    /// The installer and the packaging scripts start the program by this name.
    /// </summary>
    [Fact]
    public void AssemblyName_DesktopAssembly_IsSwiftlet()
    {
        // Arrange
        var assembly = typeof(App).Assembly;

        // Act
        var name = assembly.GetName().Name;

        // Assert
        name.Should().Be("Swiftlet");
    }
}
