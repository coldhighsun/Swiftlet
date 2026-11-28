using System.Text;

namespace Swiftlet.Desktop.Services;

/// <summary>
/// Starts the application at sign-in with a desktop entry in the XDG autostart directory.
/// </summary>
/// <param name="directory">The autostart directory.</param>
/// <param name="executablePath">The executable to start.</param>
public sealed class LinuxAutoStart(string directory, string executablePath) : IAutoStart
{
    /// <summary>
    /// The name of the desktop entry file.
    /// </summary>
    internal const string FileName = "swiftlet.desktop";

    /// <summary>
    /// The desktop entry file.
    /// </summary>
    private readonly string _path = Path.Combine(directory, FileName);

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <summary>
    /// Finds the autostart directory.
    /// </summary>
    /// <param name="configHome">The value of <c>XDG_CONFIG_HOME</c>, if set; a relative one is ignored.</param>
    /// <param name="home">The user's home directory.</param>
    /// <returns>The directory.</returns>
    internal static string AutostartDirectory(string? configHome, string home)
    {
        var root = configHome is { Length: > 0 } && Path.IsPathRooted(configHome)
            ? configHome
            : Path.Combine(home, ".config");
        return Path.Combine(root, "autostart");
    }

    /// <inheritdoc />
    public bool IsEnabled()
    {
        if (!File.Exists(_path))
        {
            return false;
        }

        var startsThisCopy = false;
        var inMainGroup = false;
        foreach (var raw in File.ReadLines(_path))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inMainGroup = line == "[Desktop Entry]";
                continue;
            }

            var separator = line.IndexOf('=');
            if (!inMainGroup || separator < 0)
            {
                continue;
            }

            var key = line[..separator].TrimEnd();
            var value = line[(separator + 1)..].TrimStart();
            // Desktop environments mark a switched off entry with one of these instead of deleting the file.
            if ((key == "Hidden" && value == "true") || (key == "X-GNOME-Autostart-enabled" && value == "false"))
            {
                return false;
            }

            if (key == "Exec")
            {
                startsThisCopy = value == ExecValue(executablePath);
            }
        }

        return startsThisCopy;
    }

    /// <inheritdoc />
    public void SetEnabled(bool enabled)
    {
        if (!enabled)
        {
            // Deleting a file that does not exist is fine, but a missing directory would throw.
            if (File.Exists(_path))
            {
                File.Delete(_path);
            }

            return;
        }

        Directory.CreateDirectory(directory);
        File.WriteAllText(
            _path,
            $"[Desktop Entry]\nType=Application\nName=Swiftlet\nExec={ExecValue(executablePath)}\nTerminal=false\n");
    }

    /// <summary>
    /// Quotes a path for the <c>Exec</c> key as the Desktop Entry Specification asks: reserved characters are escaped
    /// inside the quotes, a percent sign is doubled, and every backslash is doubled again as in any string value.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns>The value of the key.</returns>
    internal static string ExecValue(string path)
    {
        var quoted = new StringBuilder("\"");
        foreach (var character in path)
        {
            if (character is '"' or '`' or '$' or '\\')
            {
                quoted.Append('\\');
            }

            quoted.Append(character);
            if (character == '%')
            {
                quoted.Append('%');
            }
        }

        return quoted.Append('"').ToString().Replace(@"\", @"\\", StringComparison.Ordinal);
    }
}
