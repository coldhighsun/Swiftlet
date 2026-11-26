using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Swiftlet.Application.Sync;
using Swiftlet.Domain.Folders;
using Swiftlet.Infrastructure.Persistence.Repositories;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="SyncAccountCommandHandler"/> against real SQLite repositories and a scripted IMAP session.
/// </summary>
public class SyncAccountCommandTests
{
    /// <summary>
    /// Creates a handler over its own context.
    /// </summary>
    /// <param name="db">The test database.</param>
    /// <param name="factory">The IMAP factory.</param>
    /// <param name="coordinator">The shared coordinator.</param>
    /// <param name="events">The event source.</param>
    /// <returns>The handler.</returns>
    private static SyncAccountCommandHandler CreateHandler(
        TestDatabase db,
        FakeImapSessionFactory factory,
        SyncCoordinator coordinator,
        SyncEvents events)
    {
        return new SyncAccountCommandHandler(CreateService(db, factory), coordinator, events);
    }

    /// <summary>
    /// Creates a synchronization service over its own context.
    /// </summary>
    /// <param name="db">The test database.</param>
    /// <param name="factory">The IMAP factory.</param>
    /// <returns>The service.</returns>
    private static MailSyncService CreateService(TestDatabase db, FakeImapSessionFactory factory)
    {
        var context = db.CreateVerifier();
        return new MailSyncService(
            context.Accounts(),
            context.Folders(),
            context.Messages(),
            factory,
            new FakeSyncSettings(),
            new MailSyncOptions(),
            NullLogger<MailSyncService>.Instance);
    }

    /// <summary>
    /// Checking one folder raises the event only when the cache changed.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task HandleFolder_OnlyAChange_RaisesAccountSynced()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, _) = await db.SeedAccountAsync(ct);
        var factory = new FakeImapSessionFactory();
        factory.Session.Folders.Add(TestData.Folder("INBOX", FolderRole.Inbox));
        factory.Session.Messages["INBOX"] = [TestData.Remote(1)];
        var events = new SyncEvents();
        var coordinator = new SyncCoordinator();
        await CreateHandler(db, factory, coordinator, events).Handle(new SyncAccountCommand(account.Id), ct);
        var inbox = (await db.CreateFolderRepository().GetByAccountAsync(account.Id, ct)).Single();
        var raised = new List<int>();
        events.AccountSynced += (_, e) => raised.Add(e.NewMessages);
        var handler = new SyncFolderCommandHandler(CreateService(db, factory), coordinator, events);

        // Act
        await handler.Handle(new SyncFolderCommand(account.Id, inbox.Id), ct);
        var afterUnchanged = raised.Count;
        factory.Session.Messages["INBOX"].Add(TestData.Remote(2));
        await handler.Handle(new SyncFolderCommand(account.Id, inbox.Id), ct);

        // Assert
        afterUnchanged.Should().Be(0);
        raised.Should().Equal(1);
    }

    /// <summary>
    /// Checking one folder waits while the account is being synchronized, so messages are never stored twice.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task HandleFolder_AccountLockHeld_WaitsForIt()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, _) = await db.SeedAccountAsync(ct);
        var coordinator = new SyncCoordinator();
        var release = new TaskCompletionSource<bool>();
        var holder = coordinator.RunExclusiveAsync(account.Id, () => release.Task, ct);
        var handler = new SyncFolderCommandHandler(CreateService(db, new FakeImapSessionFactory()), coordinator, new SyncEvents());

        // Act
        var check = handler.Handle(new SyncFolderCommand(account.Id, Guid.NewGuid()), ct).AsTask();
        var waited = coordinator.HasWaiters(account.Id) && !check.IsCompleted;
        release.SetResult(true);
        await Task.WhenAll(holder, check);

        // Assert
        waited.Should().BeTrue();
    }

    /// <summary>
    /// A successful sync raises the event with the counts.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task Handle_SuccessfulSync_RaisesAccountSyncedWithCounts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, _) = await db.SeedAccountAsync(ct);
        var factory = new FakeImapSessionFactory();
        factory.Session.Folders.Add(TestData.Folder("INBOX", FolderRole.Inbox));
        factory.Session.Messages["INBOX"] = [TestData.Remote(1), TestData.Remote(2)];
        var events = new SyncEvents();
        var raised = new List<AccountSyncedEventArgs>();
        events.AccountSynced += (_, e) => raised.Add(e);
        var handler = CreateHandler(db, factory, new SyncCoordinator(), events);

        var result = await handler.Handle(new SyncAccountCommand(account.Id), ct);

        result.NewMessages.Should().Be(2);
        raised.Should().ContainSingle();
        raised[0].AccountId.Should().Be(account.Id);
        raised[0].NewMessages.Should().Be(2);
    }

    /// <summary>
    /// A changed INBOX is announced while the other folders are still synchronizing, and the announced counts add up.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task Handle_SeveralFolders_AnnouncesInboxBeforeTheOthersFinish()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, _) = await db.SeedAccountAsync(ct);
        var factory = new FakeImapSessionFactory();
        factory.Session.Folders.Add(TestData.Folder("INBOX", FolderRole.Inbox));
        factory.Session.Folders.Add(TestData.Folder("Other", FolderRole.Other));
        factory.Session.Messages["INBOX"] = [TestData.Remote(1), TestData.Remote(2)];
        factory.Session.Messages["Other"] = [TestData.Remote(1)];
        var gate = new CommandGate<string>();
        factory.CreateSession = _ => new FakeImapConnection(factory.Session)
        {
            BeforeFetchNewAsync = (folder, token) => folder == "Other" ? gate.WaitAsync(folder, token) : Task.CompletedTask,
        };
        var events = new SyncEvents();
        var raised = new List<int>();
        events.AccountSynced += (_, e) => raised.Add(e.NewMessages);
        var handler = CreateHandler(db, factory, new SyncCoordinator(), events);

        // Act
        var sync = handler.Handle(new SyncAccountCommand(account.Id), ct).AsTask();
        await WatcherHarness.WaitUntilAsync(() => gate.Arrived.Count == 1 && raised.Count == 1, ct);
        var whileOtherRuns = raised.ToList();
        gate.Release("Other");
        await sync;

        // Assert
        whileOtherRuns.Should().Equal(2);
        raised.Should().Equal(2, 1);
    }

    /// <summary>
    /// An INBOX in which nothing changed causes no early announcement.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task Handle_UnchangedInbox_RaisesOnlyTheFinalEvent()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, _) = await db.SeedAccountAsync(ct);
        var factory = new FakeImapSessionFactory();
        factory.Session.Folders.Add(TestData.Folder("INBOX", FolderRole.Inbox));
        factory.Session.Folders.Add(TestData.Folder("Other", FolderRole.Other));
        factory.Session.Messages["INBOX"] = [TestData.Remote(1)];
        var events = new SyncEvents();
        var handler = CreateHandler(db, factory, new SyncCoordinator(), events);
        await handler.Handle(new SyncAccountCommand(account.Id), ct);
        var raised = 0;
        events.AccountSynced += (_, _) => raised++;

        // Act
        await handler.Handle(new SyncAccountCommand(account.Id), ct);

        // Assert
        raised.Should().Be(1);
    }

    /// <summary>
    /// A flag changed on the server counts as an INBOX change and is announced early.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task Handle_InboxReadOnServer_AnnouncesEarly()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, _) = await db.SeedAccountAsync(ct);
        var factory = new FakeImapSessionFactory();
        factory.Session.Folders.Add(TestData.Folder("INBOX", FolderRole.Inbox));
        factory.Session.Folders.Add(TestData.Folder("Other", FolderRole.Other));
        factory.Session.Messages["INBOX"] = [TestData.Remote(1)];
        var events = new SyncEvents();
        var handler = CreateHandler(db, factory, new SyncCoordinator(), events);
        await handler.Handle(new SyncAccountCommand(account.Id), ct);
        factory.Session.SetServerFlags("INBOX", 1, true, false);
        var raised = 0;
        events.AccountSynced += (_, _) => raised++;

        // Act
        await handler.Handle(new SyncAccountCommand(account.Id), ct);

        // Assert
        raised.Should().Be(2);
    }

    /// <summary>
    /// The coordinator reports a waiting caller only while it waits.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task HasWaiters_SecondCallerQueued_IsTrueOnlyWhileWaiting()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var coordinator = new SyncCoordinator();
        var id = Guid.NewGuid();
        var release = new TaskCompletionSource<int>();
        var holder = coordinator.RunExclusiveAsync(id, () => release.Task, ct);
        var aloneHasWaiters = coordinator.HasWaiters(id);

        // Act
        var waiter = coordinator.RunExclusiveAsync(id, () => Task.FromResult(coordinator.HasWaiters(id)), ct);
        var queuedHasWaiters = coordinator.HasWaiters(id);
        release.SetResult(0);
        await holder;
        var waiterSawWaiters = await waiter;

        // Assert
        aloneHasWaiters.Should().BeFalse();
        queuedHasWaiters.Should().BeTrue();
        waiterSawWaiters.Should().BeFalse();
    }

    /// <summary>
    /// A failing sync raises no event.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task Handle_SyncThrows_DoesNotRaiseEvent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, _) = await db.SeedAccountAsync(ct);
        var factory = new FakeImapSessionFactory { Failure = new IOException("offline") };
        var events = new SyncEvents();
        var raised = 0;
        events.AccountSynced += (_, _) => raised++;
        var handler = CreateHandler(db, factory, new SyncCoordinator(), events);

        var act = async () => await handler.Handle(new SyncAccountCommand(account.Id), ct);

        await act.Should().ThrowAsync<IOException>();
        raised.Should().Be(0);
    }

    /// <summary>
    /// Two concurrent syncs of one account never overlap.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task Handle_ConcurrentCallsSameAccount_AreSerialized()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, _) = await db.SeedAccountAsync(ct);
        var factory = new FakeImapSessionFactory();
        factory.Session.Folders.Add(TestData.Folder("INBOX", FolderRole.Inbox));
        var active = 0;
        var maxActive = 0;
        factory.Session.OnListFolders = () =>
        {
            var now = Interlocked.Increment(ref active);
            InterlockedMax(ref maxActive, now);
            Thread.Sleep(150);
            Interlocked.Decrement(ref active);
        };
        var coordinator = new SyncCoordinator();
        var events = new SyncEvents();
        var handlerA = CreateHandler(db, factory, coordinator, events);
        var handlerB = CreateHandler(db, factory, coordinator, events);

        var a = Task.Run(async () => await handlerA.Handle(new SyncAccountCommand(account.Id), ct), ct);
        var b = Task.Run(async () => await handlerB.Handle(new SyncAccountCommand(account.Id), ct), ct);
        await Task.WhenAll(a, b);

        maxActive.Should().Be(1);
    }

    /// <summary>
    /// Raises the stored maximum to at least the candidate.
    /// </summary>
    /// <param name="location">The maximum.</param>
    /// <param name="candidate">The candidate value.</param>
    private static void InterlockedMax(ref int location, int candidate)
    {
        int current;
        do
        {
            current = Volatile.Read(ref location);
            if (candidate <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref location, candidate, current) != current);
    }
}
