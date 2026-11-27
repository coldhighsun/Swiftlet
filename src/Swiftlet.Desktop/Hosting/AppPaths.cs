namespace Swiftlet.Desktop.Hosting;

/// <summary>
/// The folders the application writes to.
/// </summary>
/// <param name="DataDirectory">The folder holding the databases, the settings and the logs.</param>
public sealed record AppPaths(string DataDirectory)
{
    /// <summary>
    /// Gets the folder holding the log files.
    /// </summary>
    public string LogDirectory => Path.Combine(DataDirectory, "logs");
}
