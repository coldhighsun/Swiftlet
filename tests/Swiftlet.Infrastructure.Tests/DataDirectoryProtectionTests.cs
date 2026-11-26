using System.Runtime.Versioning;
using AwesomeAssertions;
using Swiftlet.Infrastructure.Security;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="DataDirectoryProtection"/>.
/// </summary>
public sealed class DataDirectoryProtectionTests : IDisposable
{
    /// <summary>
    /// The directory standing in for the parent of the data directory.
    /// </summary>
    private readonly string _root = Directory.CreateTempSubdirectory("swiftlet-protect-test-").FullName;

    /// <summary>
    /// Verifies a data directory that does not exist yet is created.
    /// </summary>
    [Fact]
    public void RestrictToCurrentUser_MissingDirectory_CreatesIt()
    {
        var path = Path.Combine(_root, "data");

        DataDirectoryProtection.RestrictToCurrentUser(path);

        Directory.Exists(path).Should().BeTrue();
    }

    /// <summary>
    /// Verifies the files of an existing installation are kept.
    /// </summary>
    [Fact]
    public void RestrictToCurrentUser_ExistingDirectory_KeepsContent()
    {
        var file = Path.Combine(_root, "swiftlet.db");
        File.WriteAllText(file, "data");

        DataDirectoryProtection.RestrictToCurrentUser(_root);

        File.ReadAllText(file).Should().Be("data");
    }

    /// <summary>
    /// Verifies group and other users lose access to an existing directory.
    /// </summary>
    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void RestrictToCurrentUser_OpenDirectoryOnUnix_LeavesOwnerAccessOnly()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes do not exist on Windows.");
        File.SetUnixFileMode(
            _root,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        DataDirectoryProtection.RestrictToCurrentUser(_root);

        File.GetUnixFileMode(_root).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }
}
