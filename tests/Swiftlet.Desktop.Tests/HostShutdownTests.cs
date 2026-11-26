using System.Diagnostics;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog.Core;
using Swiftlet.Desktop.Services;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests for <see cref="HostShutdown"/>.
/// </summary>
public class HostShutdownTests
{
    /// <summary>
    /// Verifies the host gets a short shutdown timeout and stops its services side by side.
    /// </summary>
    [Fact]
    public void ConfigureHostShutdown_Always_SetsShortTimeoutAndConcurrentStop()
    {
        using var provider = new ServiceCollection().ConfigureHostShutdown().BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<HostOptions>>().Value;

        options.ShutdownTimeout.Should().Be(HostShutdown.Timeout);
        options.ServicesStopConcurrently.Should().BeTrue();
    }

    /// <summary>
    /// Verifies stopping does not depend on the caller's synchronization context, which no longer runs anything after the UI has closed.
    /// </summary>
    [Fact]
    public async Task Stop_NonPumpingSynchronizationContext_CompletesPromptly()
    {
        var ct = TestContext.Current.CancellationToken;
        var service = new YieldingService();
        using var host = BuildHost(service, TimeSpan.FromSeconds(30));
        await host.StartAsync(ct);
        var context = new NonPumpingContext();
        Exception? failure = null;

        // A background thread, so a regression fails the test instead of hanging the run.
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(context);
            failure = Record.Exception(() => HostShutdown.Stop(host, Logger.None));
        })
        {
            IsBackground = true,
        };
        thread.Start();
        var finished = thread.Join(TimeSpan.FromSeconds(10));

        finished.Should().BeTrue();
        failure.Should().BeNull();
        service.Stopped.Should().BeTrue();
        context.PostCount.Should().Be(0);
    }

    /// <summary>
    /// Verifies a service that ignores cancellation cannot keep the process alive beyond the shutdown timeout.
    /// </summary>
    [Fact]
    public async Task Stop_ServiceIgnoringCancellation_ReturnsAfterShutdownTimeout()
    {
        var ct = TestContext.Current.CancellationToken;
        using var host = BuildHost(new StubbornService(), TimeSpan.FromMilliseconds(200));
        await host.StartAsync(ct);
        var watch = Stopwatch.StartNew();

        HostShutdown.Stop(host, Logger.None);

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Builds a host running one service.
    /// </summary>
    /// <param name="service">The hosted service.</param>
    /// <param name="shutdownTimeout">The shutdown timeout.</param>
    /// <returns>The host.</returns>
    private static IHost BuildHost(IHostedService service, TimeSpan shutdownTimeout)
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Services.AddSingleton(service);
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = shutdownTimeout);
        return builder.Build();
    }

    /// <summary>
    /// A service whose stop awaits without leaving the caller's synchronization context.
    /// </summary>
    private sealed class YieldingService : IHostedService
    {
        /// <summary>
        /// Gets a value indicating whether stopping ran to the end.
        /// </summary>
        public bool Stopped { get; private set; }

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            await Task.Yield();
            Stopped = true;
        }
    }

    /// <summary>
    /// A background service that never ends.
    /// </summary>
    private sealed class StubbornService : BackgroundService
    {
        /// <inheritdoc />
        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            return new TaskCompletionSource().Task;
        }
    }

    /// <summary>
    /// Drops posted callbacks, like a dispatcher whose message loop has ended.
    /// </summary>
    private sealed class NonPumpingContext : SynchronizationContext
    {
        /// <summary>
        /// The number of callbacks posted so far.
        /// </summary>
        private int _postCount;

        /// <summary>
        /// Gets the number of callbacks posted so far.
        /// </summary>
        public int PostCount => Volatile.Read(ref _postCount);

        /// <inheritdoc />
        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _postCount);
        }
    }
}
