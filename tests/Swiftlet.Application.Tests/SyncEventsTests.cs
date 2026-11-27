using AwesomeAssertions;
using Swiftlet.Application.Sync;

namespace Swiftlet.Application.Tests;

/// <summary>
/// Tests for <see cref="SyncEvents"/>.
/// </summary>
public class SyncEventsTests
{
    /// <summary>
    /// Verifies a failing subscriber neither throws to the sync nor keeps the others from hearing about new mail.
    /// </summary>
    [Fact]
    public void RaiseNewMailArrived_FailingSubscriber_OthersStillReceive()
    {
        var events = new SyncEvents();
        var received = new List<NewMailEventArgs>();
        events.NewMailArrived += (_, _) => throw new InvalidOperationException("subscriber bug");
        events.NewMailArrived += (_, e) => received.Add(e);
        var args = new NewMailEventArgs(
            Guid.NewGuid(),
            "me@example.com",
            [new NewMailSummary("Alice", "alice@example.com", "Hello", DateTimeOffset.UnixEpoch)]);

        events.RaiseNewMailArrived(args);

        received.Should().ContainSingle().Which.Should().BeSameAs(args);
    }
}
