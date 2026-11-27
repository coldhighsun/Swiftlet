using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Sync;
using Swiftlet.Domain.Folders;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for the report of newly arrived mail by <see cref="MailSyncService"/>.
/// </summary>
public class NewMailAnnouncementTests
{
    /// <summary>
    /// The time the tests pretend it is; the fake messages are sent shortly after the start of 2026.
    /// </summary>
    private static readonly DateTimeOffset Now = new(2026, 1, 10, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Verifies the first sync of an account announces nothing.
    /// </summary>
    [Fact]
    public async Task Sync_FirstSync_AnnouncesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var scenario = await Scenario.CreateAsync(db, FolderRole.Inbox, [TestData.Remote(1)], ct);

        await scenario.SyncAsync(ct);

        scenario.Announced.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies unread mail arriving later is announced once, with what the notification shows.
    /// </summary>
    [Fact]
    public async Task Sync_NewUnreadInboxMail_AnnouncesItOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var scenario = await Scenario.CreateAsync(db, FolderRole.Inbox, [TestData.Remote(1)], ct);
        await scenario.SyncAsync(ct);
        scenario.Server.Add(TestData.Remote(2, "Lunch?"));
        scenario.Server.Add(TestData.Remote(3) with { IsRead = true });

        await scenario.SyncAsync(ct);
        await scenario.SyncAsync(ct);

        var arrival = scenario.Announced.Should().ContainSingle().Subject;
        arrival.AccountId.Should().Be(scenario.AccountId);
        arrival.AccountAddress.Should().Be(scenario.AccountAddress);
        var folder = (await db.CreateFolderRepository().GetByAccountAsync(scenario.AccountId, ct)).Single();
        var cached = (await db.CreateMessageRepository().GetByFolderAsync(scenario.AccountId, folder.Id, 0, 10, ct))
            .Single(m => m.Uid == 2);
        arrival.Messages.Should().ContainSingle().Which.Should().Be(new NewMailSummary(
            "Alice", "alice@example.com", "Lunch?", TestData.Remote(2).SentAt, cached.Id, folder.Id, cached.Preview));
    }

    /// <summary>
    /// Verifies a check of the single folder announces new mail too.
    /// </summary>
    [Fact]
    public async Task SyncSingleFolder_NewMail_AnnouncesIt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var scenario = await Scenario.CreateAsync(db, FolderRole.Inbox, [TestData.Remote(1)], ct);
        await scenario.SyncAsync(ct);
        scenario.Server.Add(TestData.Remote(2));
        var folder = (await db.CreateFolderRepository().GetByAccountAsync(scenario.AccountId, ct)).Single();

        await scenario.Service(db).SyncSingleFolderAsync(scenario.AccountId, folder.Id, ct);

        scenario.Announced.Should().ContainSingle();
    }

    /// <summary>
    /// Verifies mail arriving in other folders is not announced.
    /// </summary>
    [Fact]
    public async Task Sync_NewMailOutsideInbox_AnnouncesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var scenario = await Scenario.CreateAsync(db, FolderRole.Junk, [TestData.Remote(1)], ct);
        await scenario.SyncAsync(ct);
        scenario.Server.Add(TestData.Remote(2));

        await scenario.SyncAsync(ct);

        scenario.Announced.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies the mail downloaded again after a UIDVALIDITY change is not announced.
    /// </summary>
    [Fact]
    public async Task Sync_UidValidityChanged_AnnouncesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var scenario = await Scenario.CreateAsync(db, FolderRole.Inbox, [TestData.Remote(1)], ct);
        scenario.Factory.Session.UidValidity["INBOX"] = 1;
        await scenario.SyncAsync(ct);
        scenario.Factory.Session.UidValidity["INBOX"] = 2;

        await scenario.SyncAsync(ct);

        scenario.Announced.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies an inbox with an empty cache announces only recently sent mail.
    /// </summary>
    [Fact]
    public async Task Sync_EmptyCacheAfterEarlierSync_AnnouncesOnlyRecentMail()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var scenario = await Scenario.CreateAsync(db, FolderRole.Inbox, [], ct);
        await scenario.SyncAsync(ct);
        scenario.Server.Add(TestData.Remote(1, "old"));
        scenario.Server.Add(TestData.Remote(2, "recent") with { SentAt = Now.AddHours(-1) });

        await scenario.SyncAsync(ct);

        scenario.Announced.Should().ContainSingle().Which.Messages.Select(m => m.Subject).Should().Equal("recent");
    }

    /// <summary>
    /// A synchronized account with one folder, collecting the announcements.
    /// </summary>
    private sealed class Scenario
    {
        /// <summary>
        /// The database.
        /// </summary>
        private readonly TestDatabase _db;

        /// <summary>
        /// Receives the report of new mail.
        /// </summary>
        private readonly SyncEvents _events = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="Scenario"/> class.
        /// </summary>
        /// <param name="db">The database.</param>
        /// <param name="accountId">The account.</param>
        /// <param name="accountAddress">The account's address.</param>
        private Scenario(TestDatabase db, Guid accountId, string accountAddress)
        {
            _db = db;
            AccountId = accountId;
            AccountAddress = accountAddress;
            _events.NewMailArrived += (_, e) => Announced.Add(e);
        }

        /// <summary>
        /// Gets the account identifier.
        /// </summary>
        public Guid AccountId { get; }

        /// <summary>
        /// Gets the account's address.
        /// </summary>
        public string AccountAddress { get; }

        /// <summary>
        /// Gets the IMAP factory.
        /// </summary>
        public FakeImapSessionFactory Factory { get; } = new();

        /// <summary>
        /// Gets the messages of the server folder.
        /// </summary>
        public List<RemoteMessage> Server => Factory.Session.Messages[Factory.Session.Folders.Single().FullName];

        /// <summary>
        /// Gets the announcements, in order.
        /// </summary>
        public List<NewMailEventArgs> Announced { get; } = [];

        /// <summary>
        /// Seeds an account whose server has one folder.
        /// </summary>
        /// <param name="db">The database.</param>
        /// <param name="role">The role of the folder.</param>
        /// <param name="messages">The messages on the server.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The scenario.</returns>
        public static async Task<Scenario> CreateAsync(
            TestDatabase db,
            FolderRole role,
            List<RemoteMessage> messages,
            CancellationToken cancellationToken)
        {
            var (account, _) = await db.SeedAccountAsync(cancellationToken);
            var scenario = new Scenario(db, account.Id, account.EmailAddress);
            var name = role == FolderRole.Inbox ? "INBOX" : role.ToString();
            scenario.Factory.Session.Folders.Add(TestData.Folder(name, role));
            scenario.Factory.Session.Messages[name] = messages;
            return scenario;
        }

        /// <summary>
        /// Creates the service under test.
        /// </summary>
        /// <param name="db">The database.</param>
        /// <returns>The service.</returns>
        public MailSyncService Service(TestDatabase db)
        {
            return new MailSyncService(
                db.CreateAccountRepository(),
                db.CreateFolderRepository(),
                db.CreateMessageRepository(),
                Factory,
                new FakeSyncSettings(),
                new MailSyncOptions(),
                NullLogger<MailSyncService>.Instance,
                events: _events,
                timeProvider: new ManualTimeProvider { Now = Now });
        }

        /// <summary>
        /// Synchronizes the account.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task that completes when the sync is done.</returns>
        public async Task SyncAsync(CancellationToken cancellationToken)
        {
            await Service(_db).SyncAsync(AccountId, cancellationToken);
        }
    }
}
