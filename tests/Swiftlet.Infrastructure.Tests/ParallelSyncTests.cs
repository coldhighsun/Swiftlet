using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Sync;
using Swiftlet.Domain.Folders;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for the parallel folder synchronization of <see cref="MailSyncService"/>.
/// </summary>
public class ParallelSyncTests
{
    /// <summary>
    /// The folders of the scripted server, each holding two messages.
    /// </summary>
    private static readonly string[] FolderNames = ["Alpha", "Beta", "Gamma", "INBOX"];

    /// <summary>
    /// Verifies folders are synchronized on as many connections at once as allowed, the INBOX first, and every connection is closed.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task SyncAsync_SeveralFolders_UsesParallelConnectionsUpToTheLimit()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, _) = await db.SeedAccountAsync(ct);
        var factory = CreateServer();
        var gate = new CommandGate<string>();
        var connections = new List<FakeImapConnection>();
        factory.CreateSession = _ => Track(connections, new FakeImapConnection(factory.Session) { BeforeFetchNewAsync = gate.WaitAsync });

        // Act
        var sync = CreateService(db, factory).SyncAsync(account.Id, ct);
        await WatcherHarness.WaitUntilAsync(() => gate.Arrived.Count == 3, ct);
        var inFlight = gate.Arrived;
        gate.Release(FolderNames);
        var result = await sync;

        // Assert
        inFlight[0].Should().Be("INBOX");
        result.FoldersSynced.Should().Be(4);
        result.NewMessages.Should().Be(8);
        result.FailedFolders.Should().BeEmpty();
        factory.ConnectCount.Should().Be(3);
        connections.Should().OnlyContain(c => c.Disposed && !c.Overlapped);
    }

    /// <summary>
    /// Verifies a mailbox with one folder, or a limit of one connection, does not open further connections.
    /// </summary>
    /// <param name="folderCount">The number of folders on the server.</param>
    /// <param name="limit">The connection limit.</param>
    /// <returns>A task that completes when the test is done.</returns>
    [Theory]
    [InlineData(1, 3)]
    [InlineData(4, 1)]
    public async Task SyncAsync_NothingToShare_UsesOneConnection(int folderCount, int limit)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, _) = await db.SeedAccountAsync(ct);
        var factory = CreateServer(folderCount);

        // Act
        var result = await CreateService(db, factory, limit).SyncAsync(account.Id, ct);

        // Assert
        result.FoldersSynced.Should().Be(folderCount);
        factory.ConnectCount.Should().Be(1);
    }

    /// <summary>
    /// Verifies a server refusing further connections does not fail the sync.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task SyncAsync_FurtherConnectionsRefused_SynchronizesEveryFolder()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, _) = await db.SeedAccountAsync(ct);
        var factory = CreateServer();
        factory.CreateSession = attempt => attempt == 1 ? factory.Session : throw new IOException("too many connections");

        // Act
        var result = await CreateService(db, factory).SyncAsync(account.Id, ct);

        // Assert
        factory.ConnectCount.Should().Be(3);
        result.FoldersSynced.Should().Be(4);
        result.FailedFolders.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies a folder that failed on an additional connection is synchronized by the primary one.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task SyncAsync_AdditionalConnectionFails_PrimaryRetriesTheFolder()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, _) = await db.SeedAccountAsync(ct);
        var factory = CreateServer();
        var gate = new CommandGate<string>();
        var failures = 0;
        factory.CreateSession = attempt => new FakeImapConnection(factory.Session)
        {
            BeforeFetchNewAsync = attempt == 1
                ? gate.WaitAsync
                : (_, _) =>
                {
                    Interlocked.Increment(ref failures);
                    throw new IOException("connection reset");
                },
        };

        // Act
        var sync = CreateService(db, factory).SyncAsync(account.Id, ct);
        await WatcherHarness.WaitUntilAsync(() => Volatile.Read(ref failures) == 2, ct);
        gate.Release(FolderNames);
        var result = await sync;

        // Assert
        result.FoldersSynced.Should().Be(4);
        result.NewMessages.Should().Be(8);
        result.FailedFolders.Should().BeEmpty();
        failures.Should().Be(2, "a failed connection takes no further folder");
    }

    /// <summary>
    /// Verifies cancelling a sync ends every worker and closes every connection.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task SyncAsync_Cancelled_ClosesEveryConnection()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, _) = await db.SeedAccountAsync(ct);
        var factory = CreateServer();
        var gate = new CommandGate<string>();
        var connections = new List<FakeImapConnection>();
        factory.CreateSession = _ => Track(connections, new FakeImapConnection(factory.Session) { BeforeFetchNewAsync = gate.WaitAsync });
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Act
        var sync = CreateService(db, factory).SyncAsync(account.Id, cancel.Token);
        await WatcherHarness.WaitUntilAsync(() => gate.Arrived.Count == 3, ct);
        await cancel.CancelAsync();
        var act = () => sync;

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
        connections.Should().HaveCount(3).And.OnlyContain(c => c.Disposed);
    }

    /// <summary>
    /// Records a connection for later assertions.
    /// </summary>
    /// <param name="connections">The list of connections.</param>
    /// <param name="connection">The new connection.</param>
    /// <returns>The connection.</returns>
    private static FakeImapConnection Track(List<FakeImapConnection> connections, FakeImapConnection connection)
    {
        lock (connections)
        {
            connections.Add(connection);
        }

        return connection;
    }

    /// <summary>
    /// Creates a scripted server.
    /// </summary>
    /// <param name="folderCount">How many of the folders it has, counted from the end so the INBOX is always present.</param>
    /// <returns>The factory holding the server.</returns>
    private static FakeImapSessionFactory CreateServer(int folderCount = 4)
    {
        var factory = new FakeImapSessionFactory();
        foreach (var name in FolderNames.TakeLast(folderCount))
        {
            factory.Session.Folders.Add(TestData.Folder(name, name == "INBOX" ? FolderRole.Inbox : FolderRole.Other));
            factory.Session.Messages[name] = [TestData.Remote(1), TestData.Remote(2)];
        }

        return factory;
    }

    /// <summary>
    /// Creates the service under test.
    /// </summary>
    /// <param name="db">The test database.</param>
    /// <param name="factory">The IMAP factory.</param>
    /// <param name="limit">The connection limit.</param>
    /// <returns>The service.</returns>
    private static MailSyncService CreateService(TestDatabase db, IImapSessionFactory factory, int limit = 3)
    {
        return new MailSyncService(
            db.CreateAccountRepository(),
            db.CreateFolderRepository(),
            db.CreateMessageRepository(),
            factory,
            new FakeSyncSettings(),
            new MailSyncOptions(ConnectionsPerAccount: limit),
            NullLogger<MailSyncService>.Instance);
    }
}
