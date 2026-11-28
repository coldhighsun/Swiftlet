namespace Swiftlet.Desktop.Services;

/// <summary>
/// Registers the application to start when the user signs in. The operating system holds the only record of the choice,
/// because the user can also change it outside the application.
/// </summary>
public interface IAutoStart
{
    /// <summary>
    /// Gets a value indicating whether the application can be started at sign-in here.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Reads whether this copy of the application starts at sign-in.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if an entry exists, is not switched off, and starts the running executable.
    /// </returns>
    /// <exception cref="IOException">The entry could not be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the entry was refused.</exception>
    /// <exception cref="System.Security.SecurityException">Access to the registry was refused.</exception>
    bool IsEnabled();

    /// <summary>
    /// Creates or removes the entry. Creating one replaces an entry of another copy and lifts a switched off mark.
    /// </summary>
    /// <param name="enabled">Whether the application should start at sign-in.</param>
    /// <exception cref="IOException">The entry could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the entry was refused.</exception>
    /// <exception cref="System.Security.SecurityException">Access to the registry was refused.</exception>
    void SetEnabled(bool enabled);
}
