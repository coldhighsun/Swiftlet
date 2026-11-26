namespace Swiftlet.Infrastructure.Security;

/// <summary>
/// Keeps other local users out of the data directory, whose databases and logs are not encrypted.
/// </summary>
public static class DataDirectoryProtection
{
    /// <summary>
    /// Creates the directory if needed and, on Unix, limits access to its owner. Windows already restricts the user profile.
    /// </summary>
    /// <param name="path">The data directory.</param>
    public static void RestrictToCurrentUser(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            // Set explicitly so installations created with wider default permissions are tightened as well.
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
