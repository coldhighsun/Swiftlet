using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;

namespace Swiftlet.Desktop.Hosting;

/// <summary>
/// Stops the background services after the desktop application has closed.
/// </summary>
internal static class HostShutdown
{
    /// <summary>
    /// The longest the process waits for the background services. Abandoning unfinished work is safe: database transactions are atomic
    /// and everything still pending is stored, so the next start continues it.
    /// </summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Limits how long stopping may take and stops the background services side by side, so their delays do not add up.
    /// </summary>
    /// <param name="services">The service collection of the host.</param>
    /// <returns>The same service collection.</returns>
    public static IServiceCollection ConfigureHostShutdown(this IServiceCollection services)
    {
        return services.Configure<HostOptions>(options =>
        {
            options.ShutdownTimeout = Timeout;
            options.ServicesStopConcurrently = true;
        });
    }

    /// <summary>
    /// Stops the host and blocks until it has stopped or the shutdown timeout has passed.
    /// </summary>
    /// <param name="host">The running host.</param>
    /// <param name="logger">The logger.</param>
    public static void Stop(IHost host, ILogger logger)
    {
        var timeout = host.Services.GetRequiredService<IOptions<HostOptions>>().Value.ShutdownTimeout;
        logger.Information("Stopping background services");
        var watch = Stopwatch.StartNew();

        // The calling thread still carries the UI synchronization context, whose message loop has ended. Any continuation that
        // captured it would never run and stopping would only end by timeout, so the host is stopped on the thread pool.
        Task.Run(() => host.StopAsync()).GetAwaiter().GetResult();

        if (watch.Elapsed >= timeout)
        {
            logger.Warning("Background services did not stop within {Timeout}; exiting anyway", timeout);
        }
        else
        {
            logger.Information("Background services stopped in {ElapsedMs} ms", watch.ElapsedMilliseconds);
        }
    }
}
