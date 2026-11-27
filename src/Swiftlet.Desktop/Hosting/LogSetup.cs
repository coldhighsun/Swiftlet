using Serilog;
using Serilog.Core;
using Serilog.Events;
using Swiftlet.Application.Logging;

namespace Swiftlet.Desktop.Hosting;

/// <summary>
/// Builds the application logger: rolling files under <c>logs</c> in the data directory.
/// </summary>
internal static class LogSetup
{
    /// <summary>
    /// The largest size of one log file before a new one is started.
    /// </summary>
    public const long FileSizeLimitBytes = 10 * 1024 * 1024;

    /// <summary>
    /// The number of log files kept; older ones are deleted.
    /// </summary>
    public const int RetainedFiles = 7;

    /// <summary>
    /// The layout of one entry: the standard file layout with the account and folder tags (empty outside a mail scope)
    /// in front of the message.
    /// </summary>
    private const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}]"
        + "{" + MailLogScope.AccountProperty + ":l}{" + MailLogScope.FolderProperty + ":l}"
        + " {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Creates the logger. Files roll every day and whenever one passes the size limit.
    /// </summary>
    /// <param name="logDirectory">The directory that receives the log files.</param>
    /// <param name="fileSizeLimitBytes">The size limit of one file.</param>
    /// <param name="retainedFiles">The number of files to keep.</param>
    /// <returns>The configured logger.</returns>
    public static Logger Create(
        string logDirectory,
        long fileSizeLimitBytes = FileSizeLimitBytes,
        int retainedFiles = RetainedFiles)
    {
        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Swiftlet", LogEventLevel.Debug)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .WriteTo.File(
                Path.Combine(logDirectory, "swiftlet-.log"),
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: fileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: retainedFiles,
                outputTemplate: OutputTemplate,
                formatProvider: System.Globalization.CultureInfo.InvariantCulture)
            .CreateLogger();
    }
}
