using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Sync;
using Swiftlet.Domain.Folders;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for backfill batches that download over several connections.
/// </summary>
public class ParallelBackfillTests
{
    /// <summary>
    /// Verifies downloads overlap while messages are committed strictly from the newest to the oldest.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task BackfillBatchAsync_DownloadsFinishOutOfOrder_CommitsNewestFirst()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var s = await Scenario.CreateAsync(db, ct);
        var gate = new CommandGate<uint>();
        var primary = new FakeImapConnection(s.Server) { BeforeDownloadAsync = gate.WaitAsync };
        var helpers = new[]
        {
            new FakeImapConnection(s.Server) { BeforeDownloadAsync = gate.WaitAsync },
            new FakeImapConnection(s.Server) { BeforeDownloadAsync = gate.WaitAsync },
        };
        var stored = new List<int>();
        await using var connections = Connections(primary, helpers);

        // Act
        var batch = s.Backfill.BackfillBatchAsync(connections, s.AccountId, s.FolderId, (count, _) => stored.Add(count), ct);
        await WatcherHarness.WaitUntilAsync(() => gate.Arrived.Count == 3, ct);
        var firstWindow = gate.Arrived;
        gate.Release(3, 4);
        var storedWhileNewestPending = stored.Count;
        var cursorWhileNewestPending = await s.MinUidAsync(ct);
        gate.Release(5, 2, 1);
        var result = await batch;

        // Assert
        firstWindow.Should().BeEquivalentTo([5u, 4u, 3u]);
        storedWhileNewestPending.Should().Be(0);
        cursorWhileNewestPending.Should().Be(6u);
        result.Added.Should().Be(5);
        stored.Should().Equal(1, 2, 3, 4, 5);
        (await s.MinUidAsync(ct)).Should().Be(1u);
        new[] { primary, helpers[0], helpers[1] }.Should().OnlyContain(c => !c.Overlapped);
    }

    /// <summary>
    /// Verifies a failing additional connection is dropped and the primary one downloads its message.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task BackfillBatchAsync_AdditionalConnectionFails_PrimaryTakesOver()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var s = await Scenario.CreateAsync(db, ct);
        var primary = new FakeImapConnection(s.Server);
        var broken = new FakeImapConnection(s.Server)
        {
            BeforeDownloadAsync = (_, _) => throw new IOException("connection dropped"),
        };
        await using var connections = Connections(primary, broken);

        // Act
        var result = await s.Backfill.BackfillBatchAsync(connections, s.AccountId, s.FolderId, null, ct);

        // Assert
        result.Added.Should().Be(5);
        broken.Disposed.Should().BeTrue();
        primary.Overlapped.Should().BeFalse();
        (await connections.GetHelpersAsync(ct)).Should().BeEmpty();
        (await s.MinUidAsync(ct)).Should().Be(1u);
    }

    /// <summary>
    /// Verifies a failure of the primary connection discards messages downloaded out of order, so the cache has no gap and can resume.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task BackfillBatchAsync_PrimaryFails_LeavesNoGap()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var s = await Scenario.CreateAsync(db, ct);
        var primary = new FakeImapConnection(s.Server)
        {
            // The primary connection takes UID 5 first and fails on the next message it is given.
            BeforeDownloadAsync = (uid, _) => uid == 5 ? Task.CompletedTask : throw new IOException("offline"),
        };
        await using var connections = Connections(primary, new FakeImapConnection(s.Server), new FakeImapConnection(s.Server));

        // Act
        var act = () => s.Backfill.BackfillBatchAsync(connections, s.AccountId, s.FolderId, null, ct);
        await act.Should().ThrowAsync<IOException>();
        var cursor = await s.MinUidAsync(ct);
        var cached = await s.CachedCountAsync(ct);
        var resumed = await s.Backfill.BackfillBatchAsync(s.Server, s.AccountId, s.FolderId, ct);

        // Assert
        cached.Should().Be(26 - (int)cursor, "every UID from the cursor up to 25 is cached");
        resumed.Added.Should().Be((int)cursor - 1);
        (await s.CachedCountAsync(ct)).Should().Be(25);
    }

    /// <summary>
    /// Verifies refused connections leave the batch to the primary connection.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task BackfillBatchAsync_ConnectionsRefused_CompletesOnPrimary()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var s = await Scenario.CreateAsync(db, ct);
        var attempts = 0;
        await using var connections = new BackfillConnections(
            s.Server,
            2,
            _ =>
            {
                attempts++;
                return Task.FromException<IImapSession>(new IOException("too many connections"));
            });

        // Act
        var result = await s.Backfill.BackfillBatchAsync(connections, s.AccountId, s.FolderId, null, ct);

        // Assert
        attempts.Should().Be(2);
        result.Added.Should().Be(5);
    }

    /// <summary>
    /// Verifies no further connection is opened for a single download.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task BackfillBatchAsync_OneMessageLeft_OpensNoFurtherConnection()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var s = await Scenario.CreateAsync(db, ct, 21);
        var attempts = 0;
        await using var connections = new BackfillConnections(
            s.Server,
            2,
            _ =>
            {
                attempts++;
                return Task.FromResult<IImapSession>(s.Server);
            });

        // Act
        var result = await s.Backfill.BackfillBatchAsync(connections, s.AccountId, s.FolderId, null, ct);

        // Assert
        result.Added.Should().Be(1);
        attempts.Should().Be(0);
    }

    /// <summary>
    /// Builds the connections of a pass from prepared fakes.
    /// </summary>
    /// <param name="primary">The primary connection.</param>
    /// <param name="helpers">The additional connections.</param>
    /// <returns>The connections.</returns>
    private static BackfillConnections Connections(IImapSession primary, params IImapSession[] helpers)
    {
        var next = new Queue<IImapSession>(helpers);
        return new BackfillConnections(primary, helpers.Length, _ => Task.FromResult(next.Dequeue()));
    }

    /// <summary>
    /// An account whose INBOX has completed its first sync, so only the newest 20 messages are cached.
    /// </summary>
    /// <param name="Database">The test database.</param>
    /// <param name="Server">The scripted server.</param>
    /// <param name="Backfill">The service under test.</param>
    /// <param name="AccountId">The account identifier.</param>
    /// <param name="FolderId">The INBOX identifier.</param>
    private sealed record Scenario(TestDatabase Database, FakeImapSession Server, MailBackfillService Backfill, Guid AccountId, Guid FolderId)
    {
        /// <summary>
        /// Creates the scenario.
        /// </summary>
        /// <param name="db">The test database.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <param name="count">The number of messages on the server.</param>
        /// <returns>The scenario.</returns>
        public static async Task<Scenario> CreateAsync(TestDatabase db, CancellationToken cancellationToken, int count = 25)
        {
            var (account, _) = await db.SeedAccountAsync(cancellationToken);
            var factory = new FakeImapSessionFactory();
            factory.Session.Folders.Add(TestData.Folder("INBOX", FolderRole.Inbox));
            factory.Session.Messages["INBOX"] = [.. Enumerable.Range(1, count).Select(i => TestData.Remote((uint)i))];
            var sync = new MailSyncService(
                db.CreateAccountRepository(),
                db.CreateFolderRepository(),
                db.CreateMessageRepository(),
                factory,
                new FakeSyncSettings(),
                new MailSyncOptions(),
                NullLogger<MailSyncService>.Instance);
            await sync.SyncAsync(account.Id, cancellationToken);
            var inbox = (await db.CreateFolderRepository().GetByAccountAsync(account.Id, cancellationToken)).Single();
            var backfill = new MailBackfillService(
                db.CreateFolderRepository(),
                db.CreateMessageRepository(),
                new MailSyncOptions(),
                new FakeSyncSettings(),
                NullLogger<MailBackfillService>.Instance);
            return new Scenario(db, factory.Session, backfill, account.Id, inbox.Id);
        }

        /// <summary>
        /// Reads the lowest cached UID.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The UID.</returns>
        public Task<uint> MinUidAsync(CancellationToken cancellationToken)
        {
            return Database.CreateMessageRepository().GetMinUidAsync(AccountId, FolderId, cancellationToken);
        }

        /// <summary>
        /// Counts the cached messages.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The count.</returns>
        public async Task<int> CachedCountAsync(CancellationToken cancellationToken)
        {
            return (await Database.CreateMessageRepository().GetFlagStatesAsync(AccountId, FolderId, cancellationToken)).Count;
        }
    }
}
