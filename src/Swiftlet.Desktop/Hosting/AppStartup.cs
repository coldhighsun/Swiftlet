using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Swiftlet.Infrastructure.Persistence.Encryption;

namespace Swiftlet.Desktop.Hosting;

/// <summary>
/// Runs the part of the startup that may only happen once the databases are ready.
/// </summary>
/// <param name="host">The host of the background services.</param>
/// <param name="initializeDatabase">Creates and upgrades the database schemas.</param>
/// <param name="logger">The logger; optional.</param>
public sealed class AppStartup(IHost host, Func<IServiceProvider, Task> initializeDatabase, ILogger<AppStartup>? logger = null)
{
    /// <summary>
    /// Gets a value indicating whether the databases are initialized and the background services run.
    /// </summary>
    public bool IsStarted { get; private set; }

    /// <summary>
    /// Starts without asking the user when the databases need no conversion and have no problem.
    /// </summary>
    /// <param name="protection">Prepares the databases.</param>
    /// <returns>Whether the application started; otherwise the startup window has to take over.</returns>
    public async Task<bool> TryStartQuietlyAsync(IDatabaseProtection protection)
    {
        ArgumentNullException.ThrowIfNull(protection);

        try
        {
            var assessment = await protection.AssessAsync();
            if (assessment.Result.Problem != DatabaseProtectionProblem.None || assessment.PendingConversions > 0)
            {
                return false;
            }

            var result = await protection.PrepareAsync();
            if (result.Problem != DatabaseProtectionProblem.None)
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // No window exists yet; the startup window repeats the preparation and can show what went wrong.
            logger?.LogError(ex, "Preparing the databases failed");
            return false;
        }

        await StartAsync();
        return true;
    }

    /// <summary>
    /// Initializes the databases and starts the background services; does nothing when already started.
    /// </summary>
    /// <returns>A task that completes when the application has started.</returns>
    public async Task StartAsync()
    {
        if (IsStarted)
        {
            return;
        }

        await initializeDatabase(host.Services);
        await host.StartAsync();
        IsStarted = true;
    }
}
