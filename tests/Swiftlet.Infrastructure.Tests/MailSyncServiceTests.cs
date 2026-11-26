using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Sync;
using Swiftlet.Domain.Folders;
using Swiftlet.Infrastructure.Persistence.Repositories;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="MailSyncService"/> against real SQLite repositories and a scripted IMAP session.
/// </summary>
public class MailSyncServiceTests
{
    /// <summary>
    /// Creates the service under test.
    /// </summary>
    /// <param name="db">The test database.</param>
    /// <param name="factory">The IMAP factory.</param>
    /// <param name="maxPerFolder">The per-folder download limit.</param>
    /// <param name="settings">The sync settings, or <see langword="null"/> for defaults.</param>
    /// <param name="context">The verifier whose contexts the repositories use, or <see langword="null"/> for the test's own.</param>
    /// <returns>The service.</returns>
    private static MailSyncService CreateService(
        TestDatabase db,
        FakeImapSessionFactory factory,
        int maxPerFolder = 200,
        FakeSyncSettings? settings = null,
        DatabaseVerifier? context = null)
    {
        return new MailSyncService(
            context?.Accounts() ?? db.CreateAccountRepository(),
            context?.Folders() ?? db.CreateFolderRepository(),
            context?.Messages() ?? db.CreateMessageRepository(),
            factory,
            settings ?? new FakeSyncSettings(),
            new MailSyncOptions(maxPerFolder),
            NullLogger<MailSyncService>.Instance);
    }

    /// <summary>
    /// Creates an account with an INBOX holding the given UIDs and runs the first sync.
    /// </summary>
    /// <param name="db">The test database.</param>
    /// <param name="settings">The sync settings.</param>
    /// <param name="uids">The server UIDs.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The scenario objects.</returns>
    private static async Task<(MailSyncService Service, FakeImapSession Session, Guid AccountId, Guid FolderId)> SeedSyncedInboxAsync(
        TestDatabase db,
        FakeSyncSettings settings,
        uint[] uids,
        CancellationToken ct)
    {
        var (account, _) = await db.SeedAccountAsync(ct);
        var factory = new FakeImapSessionFactory();
        factory.Session.Folders.Add(TestData.Folder("INBOX", FolderRole.Inbox));
        factory.Session.Messages["INBOX"] = [.. uids.Select(u => TestData.Remote(u))];
        var service = CreateService(db, factory, settings: settings);
        await service.SyncAsync(account.Id, ct);
        var inbox = (await db.CreateFolderRepository().GetByAccountAsync(account.Id, ct)).Single();
        return (service, factory.Session, account.Id, inbox.Id);
    }

    /// <summary>
    /// Applies a change to the cached message with the given UID through a fresh context, like the UI does.
    /// </summary>
    /// <param name="db">The test database.</param>
    /// <param name="accountId">The owning account.</param>
    /// <param name="folderId">The folder identifier.</param>
    /// <param name="uid">The UID.</param>
    /// <param name="change">The change to apply.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task that completes when the change is saved.</returns>
    private static async Task ChangeAsync(
        TestDatabase db,
        Guid accountId,
        Guid folderId,
        uint uid,
        Action<Swiftlet.Domain.Messages.MailMessage> change,
        CancellationToken ct)
    {
        await using var verifier = db.CreateVerifier();
        var context = await verifier.AccountAsync(accountId, ct);
        var message = await context.Messages.SingleAsync(m => m.FolderId == folderId && m.Uid == uid, ct);
        change(message);
        await context.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Reads the flag states of a folder through a fresh context.
    /// </summary>
    /// <param name="db">The test database.</param>
    /// <param name="accountId">The owning account.</param>
    /// <param name="folderId">The folder identifier.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The flag states ordered by UID.</returns>
    private static async Task<IReadOnlyList<Swiftlet.Domain.Messages.MessageFlagState>> StatesAsync(
        TestDatabase db,
        Guid accountId,
        Guid folderId,
        CancellationToken ct)
    {
        await using var context = db.CreateVerifier();
        return await context.Messages().GetFlagStatesAsync(accountId, folderId, ct);
    }

    /// <summary>
    /// Reads the flag state of one message through a fresh context.
    /// </summary>
    /// <param name="db">The test database.</param>
    /// <param name="accountId">The owning account.</param>
    /// <param name="folderId">The folder identifier.</param>
    /// <param name="uid">The UID.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The flag state.</returns>
    private static async Task<Swiftlet.Domain.Messages.MessageFlagState> StateAsync(
        TestDatabase db,
        Guid accountId,
        Guid folderId,
        uint uid,
        CancellationToken ct)
    {
        return (await StatesAsync(db, accountId, folderId, ct)).Single(m => m.Uid == uid);
    }

    /// <summary>
    /// Verifies a locally read message is pushed as seen and no longer pending.
    /// </summary>
    [Fact]
    public async Task Sync_PendingRead_PushesSeenAndClearsPending()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (service, session, accountId, folderId) = await SeedSyncedInboxAsync(db, new FakeSyncSettings(), [1, 2], ct);
        await ChangeAsync(db, accountId, folderId, 1, m => m.SetRead(true), ct);

        await service.SyncAsync(accountId, ct);

        var call = session.SetFlagsCalls.Should().ContainSingle().Subject;
        call.Folder.Should().Be("INBOX");
        call.Updates.Should().Equal(new FlagUpdate(1, true, false));
        session.GetServerFlags("INBOX", 1).IsRead.Should().BeTrue();
        await using var verify = db.CreateVerifier();
        (await verify.Messages().GetPendingFlagStatesAsync(accountId, folderId, ct)).Should().BeEmpty();
    }

    /// <summary>
    /// Verifies marking a message unread pushes a cleared seen flag.
    /// </summary>
    [Fact]
    public async Task Sync_PendingUnread_PushesClearedSeen()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, _) = await db.SeedAccountAsync(ct);
        var factory = new FakeImapSessionFactory();
        factory.Session.Folders.Add(TestData.Folder("INBOX", FolderRole.Inbox));
        factory.Session.Messages["INBOX"] = [TestData.Remote(1) with { IsRead = true }];
        var service = CreateService(db, factory);
        await service.SyncAsync(account.Id, ct);
        var inbox = (await db.CreateFolderRepository().GetByAccountAsync(account.Id, ct)).Single();
        (await StateAsync(db, account.Id, inbox.Id, 1, ct)).IsRead.Should().BeTrue();
        await ChangeAsync(db, account.Id, inbox.Id, 1, m => m.SetRead(false), ct);

        await service.SyncAsync(account.Id, ct);

        factory.Session.SetFlagsCalls.Single().Updates.Should().Equal(new FlagUpdate(1, false, false));
        factory.Session.GetServerFlags("INBOX", 1).IsRead.Should().BeFalse();
        (await StateAsync(db, account.Id, inbox.Id, 1, ct)).IsFlagSyncPending.Should().BeFalse();
    }

    /// <summary>
    /// Verifies starring one message and unstarring another pushes both values.
    /// </summary>
    [Fact]
    public async Task Sync_PendingStarAndUnstar_PushesBothValues()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, _) = await db.SeedAccountAsync(ct);
        var factory = new FakeImapSessionFactory();
        factory.Session.Folders.Add(TestData.Folder("INBOX", FolderRole.Inbox));
        factory.Session.Messages["INBOX"] = [TestData.Remote(1), TestData.Remote(2)];
        factory.Session.SetServerFlags("INBOX", 2, false, true);
        var service = CreateService(db, factory);
        await service.SyncAsync(account.Id, ct);
        var inbox = (await db.CreateFolderRepository().GetByAccountAsync(account.Id, ct)).Single();
        (await StateAsync(db, account.Id, inbox.Id, 2, ct)).IsFlagged.Should().BeTrue();
        await ChangeAsync(db, account.Id, inbox.Id, 1, m => m.SetFlagged(true), ct);
        await ChangeAsync(db, account.Id, inbox.Id, 2, m => m.SetFlagged(false), ct);

        await service.SyncAsync(account.Id, ct);

        factory.Session.SetFlagsCalls.Single().Updates.Should().BeEquivalentTo(
            [new FlagUpdate(1, false, true), new FlagUpdate(2, false, false)]);
        factory.Session.GetServerFlags("INBOX", 1).IsFlagged.Should().BeTrue();
        factory.Session.GetServerFlags("INBOX", 2).IsFlagged.Should().BeFalse();
    }

    /// <summary>
    /// Verifies a failing push leaves the change pending without blocking new mail or failing the folder.
    /// </summary>
    [Fact]
    public async Task Sync_PushFails_KeepsPendingAndStillFetchesNewMessages()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (service, session, accountId, folderId) = await SeedSyncedInboxAsync(db, new FakeSyncSettings(), [1], ct);
        await ChangeAsync(db, accountId, folderId, 1, m => m.SetRead(true), ct);
        session.SetFlagsFailure = new IOException("offline");
        session.Messages["INBOX"].Add(TestData.Remote(2));

        var result = await service.SyncAsync(accountId, ct);

        result.FailedFolders.Should().BeEmpty();
        result.FoldersSynced.Should().Be(1);
        result.NewMessages.Should().Be(1);
        var kept = await StateAsync(db, accountId, folderId, 1, ct);
        kept.IsFlagSyncPending.Should().BeTrue();
        kept.IsRead.Should().BeTrue();
        await using var verify = db.CreateVerifier();
        (await verify.Messages().GetFlagStatesAsync(accountId, folderId, ct)).Select(m => m.Uid).Should().Equal(1u, 2u);
    }

    /// <summary>
    /// Verifies a change whose push failed is pushed and cleared by the next successful sync.
    /// </summary>
    [Fact]
    public async Task Sync_PushFails_ThenSucceeds_ClearsPendingOnNextSync()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (service, session, accountId, folderId) = await SeedSyncedInboxAsync(db, new FakeSyncSettings(), [1], ct);
        await ChangeAsync(db, accountId, folderId, 1, m => m.SetRead(true), ct);
        session.SetFlagsFailure = new IOException("offline");
        await service.SyncAsync(accountId, ct);
        (await StateAsync(db, accountId, folderId, 1, ct)).IsFlagSyncPending.Should().BeTrue();
        session.SetFlagsFailure = null;

        await service.SyncAsync(accountId, ct);

        var synced = await StateAsync(db, accountId, folderId, 1, ct);
        synced.IsFlagSyncPending.Should().BeFalse();
        synced.IsRead.Should().BeTrue();
        session.GetServerFlags("INBOX", 1).IsRead.Should().BeTrue();
    }

    /// <summary>
    /// Verifies a change the user makes while the push is in flight is not marked as synced and goes up next sync.
    /// </summary>
    [Fact]
    public async Task Sync_UserChangesMessageDuringPush_KeepsMessagePendingAndPushesItNextSync()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (service, session, accountId, folderId) = await SeedSyncedInboxAsync(db, new FakeSyncSettings(), [1], ct);
        await ChangeAsync(db, accountId, folderId, 1, m => m.SetRead(true), ct);
        session.OnSetFlags = (_, _) =>
        {
            session.OnSetFlags = null;
            var other = db.CreateVerifier();
            var otherContext = other.AccountAsync(accountId, ct).GetAwaiter().GetResult();
            var message = otherContext.Messages.Single(m => m.Uid == 1);
            message.SetFlagged(true);
            otherContext.SaveChanges();
            other.DisposeAsync().AsTask().GetAwaiter().GetResult();
        };

        await service.SyncAsync(accountId, ct);

        var afterFirst = await StateAsync(db, accountId, folderId, 1, ct);
        afterFirst.IsFlagSyncPending.Should().BeTrue();
        afterFirst.IsRead.Should().BeTrue();
        afterFirst.IsFlagged.Should().BeTrue();
        session.GetServerFlags("INBOX", 1).IsFlagged.Should().BeFalse();

        await service.SyncAsync(accountId, ct);

        var afterSecond = await StateAsync(db, accountId, folderId, 1, ct);
        afterSecond.IsFlagSyncPending.Should().BeFalse();
        afterSecond.IsFlagged.Should().BeTrue();
        session.SetFlagsCalls.Last().Updates.Should().Equal(new FlagUpdate(1, true, true));
        session.GetServerFlags("INBOX", 1).Should().Be((true, true, false));
    }

    /// <summary>
    /// Verifies a change the user makes while the pull is in flight is not overwritten by the server's flags.
    /// </summary>
    [Fact]
    public async Task Sync_UserChangesMessageDuringPull_NotOverwritten()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (service, session, accountId, folderId) = await SeedSyncedInboxAsync(db, new FakeSyncSettings(), [1], ct);
        session.SetServerFlags("INBOX", 1, true, false);
        session.OnFetchFlags = (_, _) =>
        {
            session.OnFetchFlags = null;
            var other = db.CreateVerifier();
            var otherContext = other.AccountAsync(accountId, ct).GetAwaiter().GetResult();
            var message = otherContext.Messages.Single(m => m.Uid == 1);
            message.SetFlagged(true);
            otherContext.SaveChanges();
            other.DisposeAsync().AsTask().GetAwaiter().GetResult();
        };

        await service.SyncAsync(accountId, ct);

        var state = await StateAsync(db, accountId, folderId, 1, ct);
        state.IsFlagged.Should().BeTrue();
        state.IsRead.Should().BeFalse();
        state.IsFlagSyncPending.Should().BeTrue();
    }

    /// <summary>
    /// Verifies the flag pull leaves no message entities in the sync scope's change tracker.
    /// </summary>
    [Fact]
    public async Task Sync_Pull_DoesNotLoadEntitiesIntoChangeTracker()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (_, session, accountId, _) = await SeedSyncedInboxAsync(db, new FakeSyncSettings(), [1, 2], ct);
        session.SetServerFlags("INBOX", 2, true, false);
        var factory = new FakeImapSessionFactory();
        factory.Session.Folders.AddRange(session.Folders);
        factory.Session.Messages["INBOX"] = session.Messages["INBOX"];
        factory.Session.SetServerFlags("INBOX", 2, true, false);
        await using var syncContext = db.CreateVerifier();

        await CreateService(db, factory, context: syncContext).SyncAsync(accountId, ct);

        (await syncContext.AccountAsync(accountId, ct)).ChangeTracker.Entries<Swiftlet.Domain.Messages.MailMessage>().Should().BeEmpty();
        factory.Session.FetchFlagsCalls.Should().NotBeEmpty();
    }

    /// <summary>
    /// Verifies a message read elsewhere becomes read locally.
    /// </summary>
    [Fact]
    public async Task Sync_ServerMarkedRead_UpdatesLocalMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (service, session, accountId, folderId) = await SeedSyncedInboxAsync(db, new FakeSyncSettings(), [1, 2], ct);
        session.SetServerFlags("INBOX", 2, true, false);

        await service.SyncAsync(accountId, ct);

        await using var verify = db.CreateVerifier();
        var cached = await verify.Messages().GetFlagStatesAsync(accountId, folderId, ct);
        cached.Single(m => m.Uid == 2).IsRead.Should().BeTrue();
        cached.Single(m => m.Uid == 1).IsRead.Should().BeFalse();
        session.SetFlagsCalls.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies a star removed elsewhere is removed locally.
    /// </summary>
    [Fact]
    public async Task Sync_ServerUnstarred_UpdatesLocalMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (service, session, accountId, folderId) = await SeedSyncedInboxAsync(db, new FakeSyncSettings(), [1], ct);
        session.SetServerFlags("INBOX", 1, false, true);
        await service.SyncAsync(accountId, ct);
        (await StateAsync(db, accountId, folderId, 1, ct)).IsFlagged.Should().BeTrue();
        session.SetServerFlags("INBOX", 1, false, false);

        await service.SyncAsync(accountId, ct);

        await using var verify = db.CreateVerifier();
        (await verify.Messages().GetFlagStatesAsync(accountId, folderId, ct)).Single().IsFlagged.Should().BeFalse();
    }

    /// <summary>
    /// Verifies the pull does not overwrite a message whose local change could not be pushed.
    /// </summary>
    [Fact]
    public async Task Sync_PendingLocalChange_NotOverwrittenByServerFlags()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (service, session, accountId, folderId) = await SeedSyncedInboxAsync(db, new FakeSyncSettings(), [1], ct);
        await ChangeAsync(db, accountId, folderId, 1, m => m.SetRead(true), ct);
        // The push fails, so the message stays pending while the server holds different values.
        session.SetFlagsFailure = new IOException("offline");
        session.SetServerFlags("INBOX", 1, false, true);
        session.FetchFlagsCalls.Clear();

        await service.SyncAsync(accountId, ct);

        session.FetchFlagsCalls.Should().ContainSingle().Which.Folder.Should().Be("INBOX");
        var state = await StateAsync(db, accountId, folderId, 1, ct);
        state.IsRead.Should().BeTrue();
        state.IsFlagged.Should().BeFalse();
        state.IsFlagSyncPending.Should().BeTrue();
    }

    /// <summary>
    /// Verifies a message missing on the server stays cached when the setting is off.
    /// </summary>
    [Fact]
    public async Task Sync_ServerDeleted_SettingOff_KeepsLocalMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (service, session, accountId, folderId) = await SeedSyncedInboxAsync(db, new FakeSyncSettings(), [1, 2], ct);
        session.RemoveFromServer("INBOX", 1);

        await service.SyncAsync(accountId, ct);

        await using var verify = db.CreateVerifier();
        (await verify.Messages().GetFlagStatesAsync(accountId, folderId, ct)).Select(m => m.Uid).Should().Equal(1u, 2u);
    }

    /// <summary>
    /// Verifies a message missing on the server is removed locally when the setting is on.
    /// </summary>
    [Fact]
    public async Task Sync_ServerDeleted_SettingOn_RemovesLocalMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (service, session, accountId, folderId) = await SeedSyncedInboxAsync(
            db,
            new FakeSyncSettings { RemoveServerDeletedMessages = true },
            [1, 2, 3],
            ct);
        session.RemoveFromServer("INBOX", 2);

        await service.SyncAsync(accountId, ct);

        await using var verify = db.CreateVerifier();
        (await verify.Messages().GetFlagStatesAsync(accountId, folderId, ct)).Select(m => m.Uid).Should().Equal(1u, 3u);
    }

    /// <summary>
    /// Verifies a message flagged deleted on the server is removed locally when the setting is on.
    /// </summary>
    [Fact]
    public async Task Sync_ServerFlagsDeleted_SettingOn_RemovesLocalMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (service, session, accountId, folderId) = await SeedSyncedInboxAsync(
            db,
            new FakeSyncSettings { RemoveServerDeletedMessages = true },
            [1, 2],
            ct);
        session.MarkDeletedOnServer("INBOX", 1);

        await service.SyncAsync(accountId, ct);

        await using var verify = db.CreateVerifier();
        (await verify.Messages().GetFlagStatesAsync(accountId, folderId, ct)).Select(m => m.Uid).Should().Equal(2u);
    }

    /// <summary>
    /// Verifies an empty flag result that the server's search contradicts is treated as a hiccup.
    /// </summary>
    [Fact]
    public async Task Sync_SettingOn_EmptyFetchButSearchFindsMessages_RemovesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (service, session, accountId, folderId) = await SeedSyncedInboxAsync(
            db,
            new FakeSyncSettings { RemoveServerDeletedMessages = true },
            [1, 2],
            ct);
        session.FetchFlagsReturnsEmpty = true;

        await service.SyncAsync(accountId, ct);

        await using var verify = db.CreateVerifier();
        (await verify.Messages().GetFlagStatesAsync(accountId, folderId, ct)).Should().HaveCount(2);
    }

    /// <summary>
    /// Verifies a folder emptied on the server is emptied locally once the search confirms it.
    /// </summary>
    [Fact]
    public async Task Sync_SettingOn_ServerFolderEmptied_RemovesAllLocalMessages()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (service, session, accountId, folderId) = await SeedSyncedInboxAsync(
            db,
            new FakeSyncSettings { RemoveServerDeletedMessages = true },
            [3, 4],
            ct);
        session.RemoveFromServer("INBOX", 3);
        session.RemoveFromServer("INBOX", 4);

        await service.SyncAsync(accountId, ct);

        session.RangeCalls.Should().Equal(("INBOX", 2u, uint.MaxValue));
        await using var verify = db.CreateVerifier();
        (await verify.Messages().GetFlagStatesAsync(accountId, folderId, ct)).Should().BeEmpty();
    }

    /// <summary>
    /// Verifies the confirming search is not sent when nothing would be removed anyway.
    /// </summary>
    [Fact]
    public async Task Sync_SettingOff_EmptyServerResult_DoesNotSearch()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (service, session, accountId, folderId) = await SeedSyncedInboxAsync(db, new FakeSyncSettings(), [1], ct);
        session.RemoveFromServer("INBOX", 1);

        await service.SyncAsync(accountId, ct);

        session.RangeCalls.Should().BeEmpty();
        (await StatesAsync(db, accountId, folderId, ct)).Should().HaveCount(1);
    }

    /// <summary>
    /// Verifies a UIDVALIDITY reset skips both the push and the pull for the folder.
    /// </summary>
    [Fact]
    public async Task Sync_UidValidityChanged_SkipsPushAndPull()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (service, session, accountId, folderId) = await SeedSyncedInboxAsync(
            db,
            new FakeSyncSettings { RemoveServerDeletedMessages = true },
            [1, 2],
            ct);
        await ChangeAsync(db, accountId, folderId, 1, m => m.SetRead(true), ct);
        session.UidValidity["INBOX"] = 999;
        session.Messages["INBOX"].Clear();
        session.FetchFlagsCalls.Clear();

        await service.SyncAsync(accountId, ct);

        session.SetFlagsCalls.Should().BeEmpty();
        session.FetchFlagsCalls.Should().BeEmpty();
        await using var verify = db.CreateVerifier();
        (await verify.Messages().GetFlagStatesAsync(accountId, folderId, ct)).Should().BeEmpty();
    }

    /// <summary>
    /// Verifies an empty cache does not trigger a flag fetch.
    /// </summary>
    [Fact]
    public async Task Sync_NoCachedMessages_DoesNotCallFetchFlags()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, _) = await db.SeedAccountAsync(ct);
        var factory = new FakeImapSessionFactory();
        factory.Session.Folders.Add(TestData.Folder("INBOX", FolderRole.Inbox));

        await CreateService(db, factory).SyncAsync(account.Id, ct);

        factory.Session.FetchFlagsCalls.Should().BeEmpty();
        factory.Session.SetFlagsCalls.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies toggling the setting between two syncs changes the second sync's behavior.
    /// </summary>
    [Fact]
    public async Task Sync_SettingChangedBetweenSyncs_TakesEffectNextSync()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var settings = new FakeSyncSettings();
        var (service, session, accountId, folderId) = await SeedSyncedInboxAsync(db, settings, [1, 2], ct);
        session.RemoveFromServer("INBOX", 1);
        await service.SyncAsync(accountId, ct);
        (await StatesAsync(db, accountId, folderId, ct)).Should().HaveCount(2);

        settings.RemoveServerDeletedMessages = true;
        await service.SyncAsync(accountId, ct);

        await using var verify = db.CreateVerifier();
        (await verify.Messages().GetFlagStatesAsync(accountId, folderId, ct)).Select(m => m.Uid).Should().Equal(2u);
    }

    /// <summary>
    /// Verifies the first sync creates selectable folders and caches their messages.
    /// </summary>
    [Fact]
    public async Task SyncAsync_FirstRun_CreatesFoldersAndCachesMessages()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, _) = await db.SeedAccountAsync(ct);
        var factory = new FakeImapSessionFactory();
        factory.Session.Folders.AddRange([
            TestData.Folder("INBOX", FolderRole.Inbox),
            TestData.Folder("Sent", FolderRole.Sent),
            new RemoteFolder("[Gmail]", "[Gmail]", FolderRole.Other, false)]);
        factory.Session.Messages["INBOX"] = [TestData.Remote(1), TestData.Remote(2), TestData.Remote(3)];

        var result = await CreateService(db, factory).SyncAsync(account.Id, ct);

        result.NewMessages.Should().Be(3);
        result.FoldersSynced.Should().Be(2);
        result.FailedFolders.Should().BeEmpty();
        var folders = await db.CreateFolderRepository().GetByAccountAsync(account.Id, ct);
        folders.Select(f => f.FullName).Should().BeEquivalentTo("INBOX", "Sent");
        folders.Single(f => f.FullName == "Sent").Role.Should().Be(FolderRole.Sent);
        factory.Session.Disposed.Should().BeTrue();
    }

    /// <summary>
    /// Verifies a second sync only requests messages newer than the cached maximum UID.
    /// </summary>
    [Fact]
    public async Task SyncAsync_SecondRun_FetchesOnlyNewMessages()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, _) = await db.SeedAccountAsync(ct);
        var factory = new FakeImapSessionFactory();
        factory.Session.Folders.Add(TestData.Folder("INBOX", FolderRole.Inbox));
        factory.Session.Messages["INBOX"] = [TestData.Remote(1), TestData.Remote(2)];
        var service = CreateService(db, factory);
        await service.SyncAsync(account.Id, ct);

        factory.Session.Messages["INBOX"].Add(TestData.Remote(3));
        var second = await service.SyncAsync(account.Id, ct);

        second.NewMessages.Should().Be(1);
        factory.Session.FetchCalls.Last().AfterUid.Should().Be(2u);
        var inbox = (await db.CreateFolderRepository().GetByAccountAsync(account.Id, ct)).Single(f => f.FullName == "INBOX");
        (await db.CreateMessageRepository().GetMaxUidAsync(account.Id, inbox.Id, ct)).Should().Be(3u);
    }

    /// <summary>
    /// Verifies a changed UIDVALIDITY discards the cache and re-downloads the folder.
    /// </summary>
    [Fact]
    public async Task SyncAsync_UidValidityChanged_DiscardsAndRefetches()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, _) = await db.SeedAccountAsync(ct);
        var factory = new FakeImapSessionFactory();
        factory.Session.Folders.Add(TestData.Folder("INBOX", FolderRole.Inbox));
        factory.Session.UidValidity["INBOX"] = 100;
        factory.Session.Messages["INBOX"] = [TestData.Remote(1, "old one"), TestData.Remote(2, "old two")];
        var service = CreateService(db, factory);
        await service.SyncAsync(account.Id, ct);

        factory.Session.UidValidity["INBOX"] = 200;
        factory.Session.Messages["INBOX"] = [TestData.Remote(1, "renumbered")];
        var result = await service.SyncAsync(account.Id, ct);

        result.NewMessages.Should().Be(1);
        var messages = db.CreateMessageRepository();
        (await messages.SearchAsync("renumbered", account.Id, 0, 10, ct)).Should().HaveCount(1);
        (await messages.SearchAsync("old", account.Id, 0, 10, ct)).Should().BeEmpty();
    }

    /// <summary>
    /// Verifies the first sync downloads only the newest messages up to the configured limit.
    /// </summary>
    [Fact]
    public async Task SyncAsync_MoreMessagesThanLimit_DownloadsNewestOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, _) = await db.SeedAccountAsync(ct);
        var factory = new FakeImapSessionFactory();
        factory.Session.Folders.Add(TestData.Folder("INBOX", FolderRole.Inbox));
        factory.Session.Messages["INBOX"] = [.. Enumerable.Range(1, 5).Select(i => TestData.Remote((uint)i))];

        var result = await CreateService(db, factory, maxPerFolder: 2).SyncAsync(account.Id, ct);

        result.NewMessages.Should().Be(2);
        var inbox = (await db.CreateFolderRepository().GetByAccountAsync(account.Id, ct)).Single();
        var cached = await db.CreateMessageRepository().GetByFolderAsync(account.Id, inbox.Id, 0, 10, ct);
        cached.Select(m => m.Uid).Should().BeEquivalentTo([5u, 4u]);
    }

    /// <summary>
    /// Verifies one failing folder is reported while the other folders still synchronize.
    /// </summary>
    [Fact]
    public async Task SyncAsync_OneFolderFails_ReportsFailureAndContinues()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, _) = await db.SeedAccountAsync(ct);
        var factory = new FakeImapSessionFactory();
        factory.Session.Folders.AddRange([TestData.Folder("Broken"), TestData.Folder("INBOX", FolderRole.Inbox)]);
        factory.Session.FailingFolders.Add("Broken");
        factory.Session.Messages["INBOX"] = [TestData.Remote(1)];

        var result = await CreateService(db, factory).SyncAsync(account.Id, ct);

        result.FailedFolders.Should().Equal("Broken");
        result.FoldersSynced.Should().Be(1);
        result.NewMessages.Should().Be(1);
    }

    /// <summary>
    /// Verifies attachment metadata and read state are cached and the body becomes searchable.
    /// </summary>
    [Fact]
    public async Task SyncAsync_MessageWithAttachment_PersistsAttachmentAndIndexesBody()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, _) = await db.SeedAccountAsync(ct);
        var factory = new FakeImapSessionFactory();
        factory.Session.Folders.Add(TestData.Folder("INBOX", FolderRole.Inbox));
        var message = TestData.Remote(1, "Invoice", "please find the quarterly numbers attached") with
        {
            IsRead = true,
            Attachments = [new RemoteAttachment("q3.xlsx", "application/vnd.ms-excel", 2048)],
        };
        factory.Session.Messages["INBOX"] = [message];

        await CreateService(db, factory).SyncAsync(account.Id, ct);

        await using var verify = db.CreateVerifier();
        var found = await verify.Messages().SearchAsync("quarterly numbers", null, 0, 10, ct);
        var cached = found.Should().ContainSingle().Subject;
        cached.IsRead.Should().BeTrue();
        cached.Attachments.Should().ContainSingle().Which.FileName.Should().Be("q3.xlsx");
    }

    /// <summary>
    /// Verifies syncing an unknown account fails with a clear error.
    /// </summary>
    [Fact]
    public async Task SyncAsync_UnknownAccount_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);

        var act = async () => await CreateService(db, new FakeImapSessionFactory()).SyncAsync(Guid.NewGuid(), ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
