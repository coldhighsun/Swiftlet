using AwesomeAssertions;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests the scripting contract of <see cref="FakeImapSession"/> for IDLE, which the inbox watcher tests rely on.
/// </summary>
public sealed class FakeImapIdleTests
{
    /// <summary>
    /// A new fake advertises IDLE support.
    /// </summary>
    [Fact]
    public void SupportsIdle_Default_IsTrue()
    {
        // Arrange
        var session = new FakeImapSession();

        // Act
        var supported = session.SupportsIdle;

        // Assert
        supported.Should().BeTrue();
    }

    /// <summary>
    /// Without a hook the first wait reports a change, like the real session, and is recorded.
    /// </summary>
    [Fact]
    public async Task WaitForInboxChange_NoHookFirstWait_ReturnsTrueAndRecordsCall()
    {
        // Arrange
        var session = new FakeImapSession();

        // Act
        var changed = await session.WaitForInboxChangeAsync("INBOX", TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        // Assert
        changed.Should().BeTrue();
        session.WaitCount.Should().Be(1);
        session.WaitFolders.Should().Equal("INBOX");
    }

    /// <summary>
    /// Without a hook later waits return immediately with no change.
    /// </summary>
    [Fact]
    public async Task WaitForInboxChange_NoHookSecondWait_ReturnsFalse()
    {
        // Arrange
        var session = new FakeImapSession();
        await session.WaitForInboxChangeAsync("INBOX", TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        // Act
        var changed = await session.WaitForInboxChangeAsync("INBOX", TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        // Assert
        changed.Should().BeFalse();
        session.WaitCount.Should().Be(2);
    }

    /// <summary>
    /// An already cancelled token makes the default fake throw, as the interface contract requires.
    /// </summary>
    [Fact]
    public async Task WaitForInboxChange_CancelledToken_ThrowsOperationCanceled()
    {
        // Arrange
        var session = new FakeImapSession();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Act
        var act = () => session.WaitForInboxChangeAsync("INBOX", TimeSpan.FromMinutes(1), cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// A non-positive maximum wait is rejected like the real session does.
    /// </summary>
    [Fact]
    public async Task WaitForInboxChange_NonPositiveMaxWait_Throws()
    {
        // Arrange
        var session = new FakeImapSession();

        // Act
        var act = () => session.WaitForInboxChangeAsync("INBOX", Timeout.InfiniteTimeSpan, TestContext.Current.CancellationToken);

        // Assert
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    /// <summary>
    /// The hook result is returned to the caller.
    /// </summary>
    [Fact]
    public async Task WaitForInboxChange_HookReturnsTrue_ReturnsTrue()
    {
        // Arrange
        var session = new FakeImapSession { OnWaitForChange = (_, _, _) => Task.FromResult(true) };

        // Act
        var changed = await session.WaitForInboxChangeAsync("INBOX", TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        // Assert
        changed.Should().BeTrue();
    }

    /// <summary>
    /// A hook failure reaches the caller and the call is still counted.
    /// </summary>
    [Fact]
    public async Task WaitForInboxChange_HookThrows_Propagates()
    {
        // Arrange
        var session = new FakeImapSession { OnWaitForChange = (_, _, _) => throw new IOException("boom") };

        // Act
        var act = () => session.WaitForInboxChangeAsync("INBOX", TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        // Assert
        await act.Should().ThrowAsync<IOException>();
        session.WaitCount.Should().Be(1);
    }
}
