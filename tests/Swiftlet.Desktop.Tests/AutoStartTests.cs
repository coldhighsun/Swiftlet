using System.Runtime.Versioning;
using AwesomeAssertions;
using Microsoft.Win32;
using Swiftlet.Desktop.Services;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests for the implementations of <see cref="IAutoStart"/>, each against a directory or registry key of its own.
/// </summary>
public sealed class AutoStartTests : IDisposable
{
    /// <summary>
    /// A temporary directory standing in for the user's autostart directory.
    /// </summary>
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"swiftlet-test-{Guid.NewGuid():N}");

    /// <summary>
    /// A registry key under the current user standing in for the one Windows reads.
    /// </summary>
    private readonly string _registryRoot = $@"Software\SwiftletTests-{Guid.NewGuid():N}";

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        if (OperatingSystem.IsWindows())
        {
            Registry.CurrentUser.DeleteSubKeyTree(_registryRoot, throwOnMissingSubKey: false);
        }
    }

    /// <summary>
    /// Verifies only the application's own executable can be registered.
    /// </summary>
    /// <param name="path">The process path.</param>
    /// <param name="expected">Whether it is accepted.</param>
    [Theory]
    [InlineData("/opt/Swiftlet/Swiftlet", true)]
    [InlineData("/apps/Swiftlet.exe", true)]
    [InlineData("/usr/bin/dotnet", false)]
    [InlineData("/apps/Swiftlet.Desktop.Tests.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsApplicationExecutable_Path_AcceptsOnlyTheApplication(string? path, bool expected)
    {
        PlatformAutoStart.IsApplicationExecutable(path).Should().Be(expected);
    }

    /// <summary>
    /// Verifies a process other than the application offers nothing.
    /// </summary>
    [Fact]
    public void Create_OtherExecutable_IsUnsupported()
    {
        var autoStart = PlatformAutoStart.Create("/usr/bin/dotnet");

        autoStart.IsSupported.Should().BeFalse();
        autoStart.IsEnabled().Should().BeFalse();
    }

    /// <summary>
    /// Verifies the Linux entry is created, recognised and removed.
    /// </summary>
    [Fact]
    public void Linux_EnableAndDisable_CreatesAndRemovesEntry()
    {
        var autoStart = new LinuxAutoStart(_root, "/opt/Swiftlet/Swiftlet");
        autoStart.IsEnabled().Should().BeFalse();
        autoStart.SetEnabled(false);

        autoStart.SetEnabled(true);

        autoStart.IsEnabled().Should().BeTrue();
        File.ReadAllText(Path.Combine(_root, LinuxAutoStart.FileName))
            .Should().Contain("Exec=\"/opt/Swiftlet/Swiftlet\"\n");

        autoStart.SetEnabled(false);

        autoStart.IsEnabled().Should().BeFalse();
        File.Exists(Path.Combine(_root, LinuxAutoStart.FileName)).Should().BeFalse();
    }

    /// <summary>
    /// Verifies an entry of another copy does not count, and enabling replaces it.
    /// </summary>
    [Fact]
    public void Linux_EntryOfAnotherCopy_IsNotEnabledUntilReplaced()
    {
        new LinuxAutoStart(_root, "/old/Swiftlet").SetEnabled(true);
        var autoStart = new LinuxAutoStart(_root, "/new/Swiftlet");

        autoStart.IsEnabled().Should().BeFalse();
        autoStart.SetEnabled(true);

        autoStart.IsEnabled().Should().BeTrue();
    }

    /// <summary>
    /// Verifies an entry a desktop environment switched off counts as disabled, and enabling lifts the mark.
    /// </summary>
    /// <param name="mark">The line the desktop adds.</param>
    [Theory]
    [InlineData("Hidden=true")]
    [InlineData("X-GNOME-Autostart-enabled=false")]
    public void Linux_SwitchedOffEntry_IsNotEnabled(string mark)
    {
        var autoStart = new LinuxAutoStart(_root, "/opt/Swiftlet/Swiftlet");
        autoStart.SetEnabled(true);
        File.AppendAllText(Path.Combine(_root, LinuxAutoStart.FileName), mark + "\n");

        autoStart.IsEnabled().Should().BeFalse();
        autoStart.SetEnabled(true);

        autoStart.IsEnabled().Should().BeTrue();
    }

    /// <summary>
    /// Verifies the characters the Desktop Entry Specification reserves are escaped.
    /// </summary>
    [Fact]
    public void Linux_ExecValue_ReservedCharacters_AreEscaped()
    {
        var value = LinuxAutoStart.ExecValue("/a b/$x`y\"z\\w%v/Swiftlet");

        value.Should().Be("\"/a b/\\\\$x\\\\`y\\\\\"z\\\\\\\\w%%v/Swiftlet\"");
    }

    /// <summary>
    /// Verifies the autostart directory follows <c>XDG_CONFIG_HOME</c> only when it is an absolute path.
    /// </summary>
    [Fact]
    public void Linux_AutostartDirectory_UsesAbsoluteConfigHomeOnly()
    {
        var configured = Path.Combine(_root, "config");
        var fallback = Path.Combine(_root, ".config", "autostart");

        LinuxAutoStart.AutostartDirectory(configured, _root).Should().Be(Path.Combine(configured, "autostart"));
        LinuxAutoStart.AutostartDirectory(null, _root).Should().Be(fallback);
        LinuxAutoStart.AutostartDirectory("relative", _root).Should().Be(fallback);
    }

    /// <summary>
    /// Verifies the launch agent is created, recognised and removed, with a path that needs XML escaping.
    /// </summary>
    [Fact]
    public void Mac_EnableAndDisable_CreatesAndRemovesAgent()
    {
        var autoStart = new MacAutoStart(_root, "/Applications/A & <B>/Swiftlet.app/Contents/MacOS/Swiftlet");
        var file = Path.Combine(_root, MacAutoStart.Label + ".plist");
        autoStart.IsEnabled().Should().BeFalse();
        autoStart.SetEnabled(false);

        autoStart.SetEnabled(true);

        autoStart.IsEnabled().Should().BeTrue();
        File.ReadAllText(file).Should().Contain("A &amp; &lt;B&gt;").And.Contain("<key>RunAtLoad</key>");

        autoStart.SetEnabled(false);

        autoStart.IsEnabled().Should().BeFalse();
        File.Exists(file).Should().BeFalse();
    }

    /// <summary>
    /// Verifies an agent of another copy does not count.
    /// </summary>
    [Fact]
    public void Mac_AgentOfAnotherCopy_IsNotEnabled()
    {
        new MacAutoStart(_root, "/old/Swiftlet").SetEnabled(true);

        new MacAutoStart(_root, "/new/Swiftlet").IsEnabled().Should().BeFalse();
    }

    /// <summary>
    /// Verifies a file that is not a readable property list counts as disabled and is replaced.
    /// </summary>
    [Fact]
    public void Mac_DamagedAgent_IsNotEnabledAndCanBeReplaced()
    {
        var autoStart = new MacAutoStart(_root, "/opt/Swiftlet");
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, MacAutoStart.Label + ".plist"), "bplist00 not xml");

        autoStart.IsEnabled().Should().BeFalse();
        autoStart.SetEnabled(true);

        autoStart.IsEnabled().Should().BeTrue();
    }

    /// <summary>
    /// Verifies the registry value is written quoted, recognised and removed.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void Windows_EnableAndDisable_WritesAndRemovesValue()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The registry only exists on Windows.");
        var autoStart = new WindowsAutoStart(@"C:\My Apps\Swiftlet.exe", _registryRoot);
        autoStart.IsEnabled().Should().BeFalse();
        autoStart.SetEnabled(false);

        autoStart.SetEnabled(true);

        autoStart.IsEnabled().Should().BeTrue();
        ReadRunValue().Should().Be("\"C:\\My Apps\\Swiftlet.exe\"");

        autoStart.SetEnabled(false);

        autoStart.IsEnabled().Should().BeFalse();
        ReadRunValue().Should().BeNull();
    }

    /// <summary>
    /// Verifies a value of another copy does not count.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void Windows_ValueOfAnotherCopy_IsNotEnabled()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The registry only exists on Windows.");
        new WindowsAutoStart(@"C:\old\Swiftlet.exe", _registryRoot).SetEnabled(true);

        new WindowsAutoStart(@"C:\new\Swiftlet.exe", _registryRoot).IsEnabled().Should().BeFalse();
    }

    /// <summary>
    /// Verifies the mark Task Manager leaves decides whether the entry counts, and enabling removes it.
    /// </summary>
    /// <param name="firstByte">The first byte of the mark.</param>
    /// <param name="expected">Whether the entry counts as enabled.</param>
    [Theory]
    [InlineData(2, true)]
    [InlineData(6, true)]
    [InlineData(3, false)]
    [InlineData(7, false)]
    [SupportedOSPlatform("windows")]
    public void Windows_StartupApprovedMark_DecidesAndIsRemovedOnEnable(byte firstByte, bool expected)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The registry only exists on Windows.");
        var autoStart = new WindowsAutoStart(@"C:\apps\Swiftlet.exe", _registryRoot);
        autoStart.SetEnabled(true);
        using (var approved = Registry.CurrentUser.CreateSubKey(_registryRoot + @"\Explorer\StartupApproved\Run"))
        {
            approved.SetValue("Swiftlet", new byte[] { firstByte, 0, 0, 0 }, RegistryValueKind.Binary);
        }

        autoStart.IsEnabled().Should().Be(expected);
        autoStart.SetEnabled(true);

        autoStart.IsEnabled().Should().BeTrue();
    }

    /// <summary>
    /// Reads the value the test wrote under its own <c>Run</c> key.
    /// </summary>
    /// <returns>The value, or <see langword="null"/> when there is none.</returns>
    [SupportedOSPlatform("windows")]
    private string? ReadRunValue()
    {
        using var run = Registry.CurrentUser.OpenSubKey(_registryRoot + @"\Run");
        return run?.GetValue("Swiftlet") as string;
    }
}
