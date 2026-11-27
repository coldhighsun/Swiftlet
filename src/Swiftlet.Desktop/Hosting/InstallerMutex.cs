namespace Swiftlet.Desktop.Hosting;

/// <summary>
/// A named mutex that exists while the application runs, so the Windows installer can wait for it to end before it
/// replaces the files.
/// </summary>
public static class InstallerMutex
{
    /// <summary>
    /// The name of the mutex; <c>installer/Swiftlet.iss</c> uses the same one.
    /// </summary>
    public const string Name = "Swiftlet-Running-5D0E3B7A-2C64-4E0B-9C1F-8A6D7B3E4F21";

    /// <summary>
    /// The held handle. It is never closed by the application: the operating system does that when the process has
    /// ended, which is exactly when its files can be replaced.
    /// </summary>
    private static Mutex? _held;

    /// <summary>
    /// Creates the mutex on Windows; elsewhere there is no installer that looks for it.
    /// </summary>
    public static void Hold()
    {
        if (OperatingSystem.IsWindows())
        {
            _held ??= new Mutex(initiallyOwned: false, Name);
        }
    }
}
