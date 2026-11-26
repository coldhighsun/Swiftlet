using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Swiftlet.Desktop.Services;
using Swiftlet.Infrastructure.Persistence.Encryption;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests for <see cref="AppStartup"/>.
/// </summary>
public sealed class AppStartupTests
{
    /// <summary>
    /// A host recording when it was started.
    /// </summary>
    /// <param name="events">The shared event list.</param>
    private sealed class RecordingHost(List<string> events) : IHost
    {
        /// <inheritdoc />
        public IServiceProvider Services { get; } = new ServiceCollection().BuildServiceProvider();

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            events.Add("host");
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public void Dispose()
        {
        }
    }

    /// <summary>
    /// What happened, in order.
    /// </summary>
    private readonly List<string> _events = [];

    /// <summary>
    /// Creates the object under test.
    /// </summary>
    /// <returns>The startup.</returns>
    private AppStartup Create()
    {
        return new AppStartup(
            new RecordingHost(_events),
            _ =>
            {
                _events.Add("database");
                return Task.CompletedTask;
            });
    }

    /// <summary>
    /// Verifies the databases are initialized before background services run.
    /// </summary>
    [Fact]
    public async Task StartAsync_Called_InitializesBeforeStartingHost()
    {
        var startup = Create();

        await startup.StartAsync();

        _events.Should().Equal("database", "host");
        startup.IsStarted.Should().BeTrue();
    }

    /// <summary>
    /// Verifies a second call does nothing.
    /// </summary>
    [Fact]
    public async Task StartAsync_Twice_StartsOnce()
    {
        var startup = Create();

        await startup.StartAsync();
        await startup.StartAsync();

        _events.Should().Equal("database", "host");
    }

    /// <summary>
    /// Verifies the application starts without a window when there is nothing to convert or decide.
    /// </summary>
    [Fact]
    public async Task TryStartQuietlyAsync_NothingToDo_PreparesAndStarts()
    {
        var protection = new FakeDatabaseProtection();

        var started = await Create().TryStartQuietlyAsync(protection);

        started.Should().BeTrue();
        protection.PrepareCalls.Should().Be(1);
        _events.Should().Equal("database", "host");
    }

    /// <summary>
    /// Verifies pending conversions and visible problems are left to the startup window.
    /// </summary>
    /// <param name="problem">The assessed problem.</param>
    /// <param name="pending">The pending conversions.</param>
    [Theory]
    [InlineData(DatabaseProtectionProblem.None, 2)]
    [InlineData(DatabaseProtectionProblem.KeyLost, 0)]
    public async Task TryStartQuietlyAsync_NeedsAttention_LeavesItToTheWindow(DatabaseProtectionProblem problem, int pending)
    {
        var protection = new FakeDatabaseProtection
        {
            Assessment = new DatabaseProtectionAssessment(new DatabaseProtectionResult(problem), pending),
        };

        var started = await Create().TryStartQuietlyAsync(protection);

        started.Should().BeFalse();
        protection.PrepareCalls.Should().Be(0);
        _events.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies a preparation that fails after a clean assessment shows the window.
    /// </summary>
    [Fact]
    public async Task TryStartQuietlyAsync_PreparationFails_DoesNotStart()
    {
        var protection = new FakeDatabaseProtection();
        protection.Results.Enqueue(new DatabaseProtectionResult(DatabaseProtectionProblem.Busy));

        var started = await Create().TryStartQuietlyAsync(protection);

        started.Should().BeFalse();
        _events.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies an unexpected failure is left to the window, which can show it.
    /// </summary>
    [Fact]
    public async Task TryStartQuietlyAsync_PreparationThrows_DoesNotStart()
    {
        var protection = new FakeDatabaseProtection { OnPrepare = _ => throw new IOException("disk gone") };

        var started = await Create().TryStartQuietlyAsync(protection);

        started.Should().BeFalse();
        _events.Should().BeEmpty();
    }
}
