namespace Swiftlet.Desktop.Services;

/// <summary>
/// Chooses how the application is started at sign-in on the current system.
/// </summary>
public static class PlatformAutoStart
{
    /// <summary>
    /// The file name of the application without its extension.
    /// </summary>
    private const string ExecutableName = "Swiftlet";

    /// <summary>
    /// Creates the implementation for the current system and the running executable.
    /// </summary>
    /// <param name="executablePath">The path of the running executable, usually <see cref="Environment.ProcessPath"/>.</param>
    /// <returns>The implementation; an unsupported one when the process is not the application's own executable.</returns>
    public static IAutoStart Create(string? executablePath)
    {
        if (!IsApplicationExecutable(executablePath))
        {
            return new UnsupportedAutoStart();
        }

        if (OperatingSystem.IsWindows())
        {
            return new WindowsAutoStart(executablePath);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            return new UnsupportedAutoStart();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacAutoStart(Path.Combine(home, "Library", "LaunchAgents"), executablePath);
        }

        return OperatingSystem.IsLinux()
            ? new LinuxAutoStart(
                LinuxAutoStart.AutostartDirectory(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"), home),
                executablePath)
            : new UnsupportedAutoStart();
    }

    /// <summary>
    /// Tells whether a path is the application's executable. A run through the <c>dotnet</c> host is not: starting that
    /// host at sign-in would start nothing.
    /// </summary>
    /// <param name="executablePath">The path.</param>
    /// <returns><see langword="true"/> if it can be registered.</returns>
    internal static bool IsApplicationExecutable([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? executablePath)
    {
        var name = Path.GetFileName(executablePath);
        return string.Equals(name, ExecutableName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, ExecutableName + ".exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Used where the application cannot be started at sign-in.
    /// </summary>
    private sealed class UnsupportedAutoStart : IAutoStart
    {
        /// <inheritdoc />
        public bool IsSupported => false;

        /// <inheritdoc />
        public bool IsEnabled()
        {
            return false;
        }

        /// <inheritdoc />
        public void SetEnabled(bool enabled)
        {
            // Nothing offers the choice when it is unsupported, so there is nothing to change.
        }
    }
}
