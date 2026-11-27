namespace Swiftlet.Desktop.Services;

/// <summary>
/// What the update check is doing or found last.
/// </summary>
public enum UpdateStatus
{
    /// <summary>
    /// Nothing was checked yet.
    /// </summary>
    Idle,

    /// <summary>
    /// A check is running.
    /// </summary>
    Checking,

    /// <summary>
    /// There is nothing newer.
    /// </summary>
    UpToDate,

    /// <summary>
    /// A newer version is waiting.
    /// </summary>
    Available,

    /// <summary>
    /// The installer is being downloaded or started.
    /// </summary>
    Installing,

    /// <summary>
    /// The last check failed and nothing newer is known.
    /// </summary>
    Failed,
}

/// <summary>
/// A newer version of the application.
/// </summary>
/// <param name="Version">The version, without build metadata.</param>
/// <param name="Notes">The release notes, if the release has any.</param>
/// <param name="ReleasePage">The page of the release, for downloading by hand.</param>
public sealed record AvailableVersion(string Version, string? Notes, Uri ReleasePage);

/// <summary>
/// How far a download has come.
/// </summary>
/// <param name="BytesReceived">The bytes received.</param>
/// <param name="TotalBytes">The size of the file, if known.</param>
/// <param name="BytesPerSecond">The speed.</param>
public sealed record UpdateProgress(long BytesReceived, long? TotalBytes, double BytesPerSecond);

/// <summary>
/// Looks for newer releases of the application and installs them. Use it from the UI thread.
/// </summary>
public interface IUpdateService
{
    /// <summary>
    /// Gets a value indicating whether this build looks for updates at all; development builds do not.
    /// </summary>
    bool IsSupported { get; }

    /// <summary>
    /// Gets what the update check is doing or found last.
    /// </summary>
    UpdateStatus Status { get; }

    /// <summary>
    /// Gets the newer version that is known, or <see langword="null"/>.
    /// </summary>
    AvailableVersion? Available { get; }

    /// <summary>
    /// Gets a value indicating whether this copy of the application can install <see cref="Available"/> itself.
    /// </summary>
    bool CanInstall { get; }

    /// <summary>
    /// Gets the progress of the running download, or <see langword="null"/> before its first report.
    /// </summary>
    UpdateProgress? Progress { get; }

    /// <summary>
    /// Gets why the last check the user asked for failed, or <see langword="null"/>.
    /// </summary>
    Exception? CheckError { get; }

    /// <summary>
    /// Gets why the last download or installation failed, or <see langword="null"/>.
    /// </summary>
    Exception? InstallError { get; }

    /// <summary>
    /// Raised when anything but <see cref="Progress"/> changed.
    /// </summary>
    event EventHandler? Changed;

    /// <summary>
    /// Raised when <see cref="Progress"/> changed.
    /// </summary>
    event EventHandler? ProgressChanged;

    /// <summary>
    /// Raised when an automatic check found a version, once per version.
    /// </summary>
    event EventHandler? Announced;

    /// <summary>
    /// Raised when the installer was started; the application is expected to quit.
    /// </summary>
    event EventHandler? InstallerStarted;

    /// <summary>
    /// Checks shortly after the start and then regularly, as long as the setting says so, until cancelled.
    /// </summary>
    /// <param name="cancellationToken">Stops the checks.</param>
    /// <returns>A task that completes when cancelled.</returns>
    Task RunAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Checks now because the user asked; a skipped version is offered again.
    /// </summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>A task that completes when the check is done.</returns>
    Task CheckAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Downloads, verifies and starts the installer of <see cref="Available"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancels the download.</param>
    /// <returns>
    /// <see langword="true"/> if the installer was started; otherwise see <see cref="InstallError"/>.
    /// </returns>
    Task<bool> InstallAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stops offering <see cref="Available"/> until the user checks again.
    /// </summary>
    /// <returns>A task that completes when the choice is recorded.</returns>
    Task SkipAsync();
}
