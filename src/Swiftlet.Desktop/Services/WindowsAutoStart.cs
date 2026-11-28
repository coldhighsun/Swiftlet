using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Swiftlet.Desktop.Services;

/// <summary>
/// Starts the application at sign-in through the current user's <c>Run</c> registry key.
/// </summary>
/// <param name="executablePath">The executable to start.</param>
/// <param name="rootKey">The key under the current user that holds Windows' settings; tests use one of their own.</param>
[SupportedOSPlatform("windows")]
public sealed class WindowsAutoStart(
    string executablePath,
    string rootKey = WindowsAutoStart.DefaultRootKey) : IAutoStart
{
    /// <summary>
    /// The key Windows reads at sign-in, relative to the current user.
    /// </summary>
    private const string DefaultRootKey = @"Software\Microsoft\Windows\CurrentVersion";

    /// <summary>
    /// The name of the value; the installer removes the same one when uninstalling.
    /// </summary>
    private const string ValueName = "Swiftlet";

    /// <summary>
    /// The key listing what starts at sign-in.
    /// </summary>
    private readonly string _runKey = rootKey + @"\Run";

    /// <summary>
    /// The key where Task Manager and the Windows settings record switched off entries.
    /// </summary>
    private readonly string _approvedKey = rootKey + @"\Explorer\StartupApproved\Run";

    /// <summary>
    /// The command line stored in the registry; quoted because the path may contain spaces.
    /// </summary>
    private readonly string _command = $"\"{executablePath}\"";

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public bool IsEnabled()
    {
        using var run = Registry.CurrentUser.OpenSubKey(_runKey);
        if (run?.GetValue(ValueName) is not string command
            || !string.Equals(command, _command, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // The first byte is even for an allowed entry and odd for one the user switched off.
        using var approved = Registry.CurrentUser.OpenSubKey(_approvedKey);
        return approved?.GetValue(ValueName) is not byte[] { Length: > 0 } state || (state[0] & 1) == 0;
    }

    /// <inheritdoc />
    public void SetEnabled(bool enabled)
    {
        // A leftover switched off mark would keep a new entry from running; without an entry it means nothing.
        using (var approved = Registry.CurrentUser.OpenSubKey(_approvedKey, writable: true))
        {
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);
        }

        if (enabled)
        {
            using var run = Registry.CurrentUser.CreateSubKey(_runKey);
            run.SetValue(ValueName, _command, RegistryValueKind.String);
            return;
        }

        using var existing = Registry.CurrentUser.OpenSubKey(_runKey, writable: true);
        existing?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
