using AwesomeAssertions;
using Swiftlet.Application.Sync;

namespace Swiftlet.Application.Tests;

/// <summary>
/// Tests for <see cref="SyncCoordinator"/> and <see cref="SyncEvents"/>.
/// </summary>
public class SyncCoordinatorTests
{
    /// <summary>
    /// A second call for the same account must wait until the first completes.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task RunExclusiveAsync_TwoCallsSameAccount_RunOneAtATime()
    {
        var ct = TestContext.Current.CancellationToken;
        var coordinator = new SyncCoordinator();
        var accountId = Guid.NewGuid();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = false;

        var first = coordinator.RunExclusiveAsync(
            accountId,
            async () =>
            {
                firstStarted.SetResult();
                await gate.Task;
                return 1;
            },
            ct);
        await firstStarted.Task;
        var second = coordinator.RunExclusiveAsync(
            accountId,
            () =>
            {
                secondStarted = true;
                return Task.FromResult(2);
            },
            ct);

        await Task.Delay(100, ct);
        secondStarted.Should().BeFalse();
        gate.SetResult();
        (await first).Should().Be(1);
        (await second).Should().Be(2);
        secondStarted.Should().BeTrue();
    }

    /// <summary>
    /// Different accounts must not block each other.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task RunExclusiveAsync_DifferentAccounts_RunConcurrently()
    {
        var ct = TestContext.Current.CancellationToken;
        var coordinator = new SyncCoordinator();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = coordinator.RunExclusiveAsync(
            Guid.NewGuid(),
            async () =>
            {
                firstStarted.SetResult();
                await gate.Task;
                return 1;
            },
            ct);
        await firstStarted.Task;
        var second = await coordinator.RunExclusiveAsync(Guid.NewGuid(), () => Task.FromResult(2), ct);

        second.Should().Be(2);
        first.IsCompleted.Should().BeFalse();
        gate.SetResult();
        (await first).Should().Be(1);
    }

    /// <summary>
    /// A throwing action must release the account for the next caller.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task RunExclusiveAsync_ActionThrows_ReleasesForNextCaller()
    {
        var ct = TestContext.Current.CancellationToken;
        var coordinator = new SyncCoordinator();
        var accountId = Guid.NewGuid();

        var act = () => coordinator.RunExclusiveAsync<int>(accountId, () => throw new InvalidOperationException("boom"), ct);
        await act.Should().ThrowAsync<InvalidOperationException>();
        var next = await coordinator.RunExclusiveAsync(accountId, () => Task.FromResult(7), ct).WaitAsync(TimeSpan.FromSeconds(5), ct);

        next.Should().Be(7);
    }

    /// <summary>
    /// Cancelling while waiting must throw and never run the action.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task RunExclusiveAsync_CancelledWhileWaiting_ThrowsOperationCanceledAndDoesNotRunAction()
    {
        var ct = TestContext.Current.CancellationToken;
        var coordinator = new SyncCoordinator();
        var accountId = Guid.NewGuid();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ran = false;
        var first = coordinator.RunExclusiveAsync(
            accountId,
            async () =>
            {
                firstStarted.SetResult();
                await gate.Task;
                return 1;
            },
            ct);
        await firstStarted.Task;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var waiting = coordinator.RunExclusiveAsync(
            accountId,
            () =>
            {
                ran = true;
                return Task.FromResult(2);
            },
            cts.Token);
        await cts.CancelAsync();

        await waiting.Invoking(w => w).Should().ThrowAsync<OperationCanceledException>();
        gate.SetResult();
        await first;
        ran.Should().BeFalse();
    }

    /// <summary>
    /// A throwing subscriber must not stop other subscribers or the caller.
    /// </summary>
    [Fact]
    public void RaiseAccountSynced_SubscriberThrows_OthersStillNotified()
    {
        var events = new SyncEvents();
        var accountId = Guid.NewGuid();
        AccountSyncedEventArgs? received = null;
        events.AccountSynced += (_, _) => throw new InvalidOperationException("bad subscriber");
        events.AccountSynced += (_, e) => received = e;

        events.RaiseAccountSynced(accountId, 3);

        received.Should().NotBeNull();
        received.AccountId.Should().Be(accountId);
        received.NewMessages.Should().Be(3);
    }
}
