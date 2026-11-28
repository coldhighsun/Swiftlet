namespace Swiftlet.Desktop.Hosting;

/// <summary>
/// The steps of the startup, in the order they happen.
/// </summary>
public enum StartupStage
{
    /// <summary>
    /// The databases and their encryption are being checked.
    /// </summary>
    CheckingDatabase,

    /// <summary>
    /// The shared mail database is being opened and upgraded.
    /// </summary>
    OpeningDatabase,

    /// <summary>
    /// The database of one account is being opened and upgraded.
    /// </summary>
    OpeningAccount,

    /// <summary>
    /// The background services are being started.
    /// </summary>
    StartingServices,

    /// <summary>
    /// The main window is being created.
    /// </summary>
    LoadingWindow,
}

/// <summary>
/// A report that the startup reached a step.
/// </summary>
/// <param name="Stage">The step.</param>
/// <param name="Current">The position of the account being opened, starting at one; zero for other steps.</param>
/// <param name="Total">The number of accounts to open; zero for other steps.</param>
public sealed record StartupProgress(StartupStage Stage, int Current = 0, int Total = 0);
