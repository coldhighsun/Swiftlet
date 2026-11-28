using AwesomeAssertions;
using Swiftlet.Desktop.Hosting;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Tests for <see cref="SplashStartup"/>.
/// </summary>
public sealed class SplashStartupTests
{
    /// <summary>
    /// A time provider whose single timer fires only when the test says so.
    /// </summary>
    private sealed class ManualTimer : TimeProvider, ITimer
    {
        /// <summary>
        /// Runs when the timer fires.
        /// </summary>
        private TimerCallback? _callback;

        /// <summary>
        /// The argument of the callback.
        /// </summary>
        private object? _state;

        /// <summary>
        /// Gets the delay the timer was created with.
        /// </summary>
        public TimeSpan DueTime { get; private set; }

        /// <inheritdoc />
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            DueTime = dueTime;
            return this;
        }

        /// <summary>
        /// Lets the delay pass.
        /// </summary>
        public void Elapse()
        {
            _callback!(_state);
        }

        /// <inheritdoc />
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            return true;
        }

        /// <inheritdoc />
        public void Dispose()
        {
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// The minimum duration used by the tests.
    /// </summary>
    private static readonly TimeSpan Minimum = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Verifies a preparation that ends at once is held back until the minimum duration has passed.
    /// </summary>
    /// <param name="result">The result of the preparation.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RunAsync_FastPreparation_WaitsForTheMinimumDuration(bool result)
    {
        var timer = new ManualTimer();
        var prepared = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = SplashStartup.RunAsync(
            () =>
            {
                prepared.SetResult();
                return Task.FromResult(result);
            },
            Minimum,
            timer);
        await prepared.Task.WaitAsync(TestContext.Current.CancellationToken);

        run.IsCompleted.Should().BeFalse();
        timer.DueTime.Should().Be(Minimum);
        timer.Elapse();
        (await run.WaitAsync(TestContext.Current.CancellationToken)).Should().Be(result);
    }

    /// <summary>
    /// Verifies a preparation longer than the minimum duration ends the wait when it ends.
    /// </summary>
    [Fact]
    public async Task RunAsync_SlowPreparation_WaitsForIt()
    {
        var timer = new ManualTimer();
        var preparation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = SplashStartup.RunAsync(() => preparation.Task, Minimum, timer);
        timer.Elapse();

        run.IsCompleted.Should().BeFalse();
        preparation.SetResult(true);
        (await run.WaitAsync(TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    /// <summary>
    /// Verifies a failed preparation is passed on to the caller.
    /// </summary>
    [Fact]
    public async Task RunAsync_PreparationThrows_Throws()
    {
        var run = () => SplashStartup.RunAsync(
            () => throw new IOException("disk gone"),
            TimeSpan.Zero,
            TimeProvider.System);

        await run.Should().ThrowAsync<IOException>();
    }
}
