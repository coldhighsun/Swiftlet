using AwesomeAssertions;
using Swiftlet.Desktop.Hosting;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests for <see cref="SingleInstance"/> with real lock files and pipes.
/// </summary>
public class SingleInstanceTests : IDisposable
{
    /// <summary>
    /// How long an attempt that is expected to succeed may take.
    /// </summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The temporary data directory, which does not exist yet.
    /// </summary>
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"swiftlet-test-{Guid.NewGuid():N}");

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Verifies the first instance of a data directory continues to start.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task AcquireAsync_NoOtherInstance_IsPrimary()
    {
        var (outcome, instance) = await SingleInstance.AcquireAsync(_directory, Patience, TestContext.Current.CancellationToken);
        using var held = instance;

        outcome.Should().Be(SingleInstanceOutcome.Primary);
        held.Should().NotBeNull();
    }

    /// <summary>
    /// Verifies a second instance asks the first one, every time, to show its window.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task AcquireAsync_SecondInstance_ActivatesTheFirst()
    {
        var token = TestContext.Current.CancellationToken;
        var (_, first) = await SingleInstance.AcquireAsync(_directory, Patience, token);
        using var held = first!;
        var requests = 0;
        held.SetActivationHandler(() => Interlocked.Increment(ref requests));

        var second = await SingleInstance.AcquireAsync(_directory, Patience, token);
        var third = await SingleInstance.AcquireAsync(_directory, Patience, token);

        second.Should().Be((SingleInstanceOutcome.Forwarded, (SingleInstance?)null));
        third.Outcome.Should().Be(SingleInstanceOutcome.Forwarded);
        requests.Should().Be(2, "the request is delivered before the caller gets its answer");
    }

    /// <summary>
    /// Verifies a request that arrives while the first instance is still starting is not lost.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task SetActivationHandler_EarlierRequest_IsDeliveredOnce()
    {
        var token = TestContext.Current.CancellationToken;
        var (_, first) = await SingleInstance.AcquireAsync(_directory, Patience, token);
        using var held = first!;
        await SingleInstance.AcquireAsync(_directory, Patience, token);
        var requests = 0;

        held.SetActivationHandler(() => requests++);
        held.SetActivationHandler(() => requests++);

        requests.Should().Be(1);
    }

    /// <summary>
    /// Verifies an instance that is quitting neither answers nor lets another one start while it holds the data.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task AcquireAsync_FirstStoppedListening_IsUnavailable()
    {
        var token = TestContext.Current.CancellationToken;
        var (_, first) = await SingleInstance.AcquireAsync(_directory, Patience, token);
        using var held = first!;
        held.StopListening();

        var second = await SingleInstance.AcquireAsync(_directory, TimeSpan.Zero, token);

        second.Should().Be((SingleInstanceOutcome.Unavailable, (SingleInstance?)null));
    }

    /// <summary>
    /// Verifies a new instance waits for a quitting one and then takes its place.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task AcquireAsync_FirstQuitsMeanwhile_BecomesPrimary()
    {
        var token = TestContext.Current.CancellationToken;
        var (_, first) = await SingleInstance.AcquireAsync(_directory, Patience, token);
        first!.StopListening();
        var waiting = SingleInstance.AcquireAsync(_directory, Patience, token);

        first.Dispose();
        var (outcome, second) = await waiting;
        using var held = second;

        outcome.Should().Be(SingleInstanceOutcome.Primary);
    }

    /// <summary>
    /// Verifies instances with different data run side by side.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task AcquireAsync_OtherDataDirectory_IsPrimaryToo()
    {
        var token = TestContext.Current.CancellationToken;
        var (_, first) = await SingleInstance.AcquireAsync(Path.Combine(_directory, "a"), Patience, token);
        using var heldFirst = first;

        var (outcome, second) = await SingleInstance.AcquireAsync(Path.Combine(_directory, "b"), Patience, token);
        using var heldSecond = second;

        outcome.Should().Be(SingleInstanceOutcome.Primary);
    }
}
