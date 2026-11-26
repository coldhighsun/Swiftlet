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
        var context = db.CreateVerifier();
        var service = new MailSyncService(
            context.Accounts(),
            context.Folders(),
            context.Messages(),
            factory,
            new FakeSyncSettings(),
            new MailSyncOptions(),
            NullLogger<MailSyncService>.Instance);
        return new SyncAccountCommandHandler(service, coordinator, events);
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
