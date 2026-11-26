using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Swiftlet.Application.Sync;
using Microsoft.EntityFrameworkCore;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for the first-sync limit and <see cref="MailBackfillService"/> against real SQLite repositories and a scripted IMAP session.
/// </summary>
public class MailBackfillServiceTests
{
    /// <summary>
    /// The batch size used by the tests.
    /// </summary>
    private const int BatchSize = 100;

    /// <summary>
    /// Creates the regular sync service.
    /// </summary>
    /// <param name="db">The test database.</param>
    /// <param name="factory">The IMAP factory.</param>
    /// <returns>The service.</returns>
    private static MailSyncService CreateSync(TestDatabase db, FakeImapSessionFactory factory)
    {
        return new MailSyncService(
            db.CreateAccountRepository(),
            db.CreateFolderRepository(),
            db.CreateMessageRepository(),
            factory,
            new FakeSyncSettings(),
            new MailSyncOptions(BackfillBatchSize: BatchSize),
            NullLogger<MailSyncService>.Instance);
    }

    /// <summary>
    /// Creates the backfill service.
    /// </summary>
    /// <param name="db">The test database.</param>
    /// <param name="settings">The sync settings, or <see langword="null"/> for defaults.</param>
    /// <returns>The service.</returns>
    private static MailBackfillService CreateBackfill(TestDatabase db, FakeSyncSettings? settings = null)
    {
        return new MailBackfillService(
            db.CreateFolderRepository(),
            db.CreateMessageRepository(),
            new MailSyncOptions(BackfillBatchSize: BatchSize),
            settings ?? new FakeSyncSettings(),
            NullLogger<MailBackfillService>.Instance);
    }

    /// <summary>
    /// Seeds an account whose INBOX holds the UIDs 1..count on the server, and runs the first sync.
    /// </summary>
    /// <param name="db">The test database.</param>
    /// <param name="count">The number of messages on the server.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The scenario objects.</returns>
    private static async Task<(FakeImapSession Session, Guid AccountId, Guid FolderId, SyncResult First)> SeedFirstSyncAsync(
        TestDatabase db,
        int count,
        CancellationToken ct)
    {
        var (account, _) = await db.SeedAccountAsync(ct);
        var factory = new FakeImapSessionFactory();
        factory.Session.Folders.Add(TestData.Folder("INBOX", FolderRole.Inbox));
        factory.Session.Messages["INBOX"] = [.. Enumerable.Range(1, count).Select(i => TestData.Remote((uint)i))];
        var first = await CreateSync(db, factory).SyncAsync(account.Id, ct);
        var inbox = (await db.CreateFolderRepository().GetByAccountAsync(account.Id, ct)).Single();
        return (factory.Session, account.Id, inbox.Id, first);
    }

    /// <summary>
    /// Verifies the first sync of an empty folder downloads only the newest 20 messages.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task SyncAsync_FirstSyncOfLargeFolder_FetchesOnlyNewest20()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);

        // Act
        var (session, accountId, folderId, first) = await SeedFirstSyncAsync(db, 250, ct);

        // Assert
        first.NewMessages.Should().Be(20);
        session.FetchCalls.Should().ContainSingle().Which.MaxCount.Should().Be(20);
        var messages = db.CreateMessageRepository();
        (await messages.GetMinUidAsync(accountId, folderId, ct)).Should().Be(231u);
        (await messages.GetMaxUidAsync(accountId, folderId, ct)).Should().Be(250u);
    }

    /// <summary>
    /// Verifies a later sync that finds new mail is still limited by the per-sync cap, not by the first-sync limit.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task SyncAsync_NewMailAfterFirstSync_UsesPerSyncCap()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, _) = await db.SeedAccountAsync(ct);
        var factory = new FakeImapSessionFactory();
        factory.Session.Folders.Add(TestData.Folder("INBOX", FolderRole.Inbox));
        factory.Session.Messages["INBOX"] = [.. Enumerable.Range(1, 5).Select(i => TestData.Remote((uint)i))];
        var sync = CreateSync(db, factory);
        await sync.SyncAsync(account.Id, ct);
        factory.Session.Messages["INBOX"].AddRange(Enumerable.Range(6, 60).Select(i => TestData.Remote((uint)i)));

        // Act
        var second = await sync.SyncAsync(account.Id, ct);

        // Assert
        second.NewMessages.Should().Be(60);
        factory.Session.FetchCalls.Last().MaxCount.Should().Be(200);
    }

    /// <summary>
    /// Verifies the backfill walks back from the oldest cached message in batches until nothing older is left.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task BackfillBatchAsync_RepeatedCalls_DownloadsEveryOlderMessageOnce()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (session, accountId, folderId, _) = await SeedFirstSyncAsync(db, 250, ct);
        var backfill = CreateBackfill(db);

        // Act
        var first = await backfill.BackfillBatchAsync(session, accountId, folderId, ct);
        var second = await backfill.BackfillBatchAsync(session, accountId, folderId, ct);
        var third = await backfill.BackfillBatchAsync(session, accountId, folderId, ct);

        // Assert
        first.Should().Be(new BackfillBatchResult(BackfillOutcome.Progressed, 100, 130));
        second.Should().Be(new BackfillBatchResult(BackfillOutcome.Progressed, 100, 30));
        third.Should().Be(new BackfillBatchResult(BackfillOutcome.Completed, 30, 0));
        var messages = db.CreateMessageRepository();
        (await messages.GetMinUidAsync(accountId, folderId, ct)).Should().Be(1u);
        await using var verifier = db.CreateVerifier();
        var context = await verifier.AccountAsync(accountId, ct);
        context.Messages.Count(m => m.FolderId == folderId).Should().Be(250);
        context.Messages.Select(m => m.Uid).Distinct().Count().Should().Be(250);
        session.FetchOlderCalls.Select(c => c.BeforeUid).Should().Equal(231u, 131u, 31u);
    }

    /// <summary>
    /// Verifies a completed folder reports completion again instead of fetching.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task BackfillBatchAsync_NothingOlderOnServer_ReturnsCompleted()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (session, accountId, folderId, _) = await SeedFirstSyncAsync(db, 5, ct);

        // Act
        var result = await CreateBackfill(db).BackfillBatchAsync(session, accountId, folderId, ct);

        // Assert
        result.Should().Be(new BackfillBatchResult(BackfillOutcome.Completed, 0, 0));
    }

    /// <summary>
    /// Verifies a folder that was synced but holds no messages is left to the regular sync.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task BackfillBatchAsync_SyncedEmptyFolder_ReturnsNotStarted()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (session, accountId, folderId, _) = await SeedFirstSyncAsync(db, 0, ct);

        // Act
        var result = await CreateBackfill(db).BackfillBatchAsync(session, accountId, folderId, ct);

        // Assert
        result.Should().Be(new BackfillBatchResult(BackfillOutcome.NotStarted, 0, 0));
        session.FetchOlderCalls.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies a folder that never synchronized is skipped, because its UIDVALIDITY is unknown.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task BackfillBatchAsync_FolderNeverSynced_ReturnsSkipped()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await db.SeedAccountAsync(ct);
        var session = new FakeImapSession();

        // Act
        var result = await CreateBackfill(db).BackfillBatchAsync(session, account.Id, folder.Id, ct);

        // Assert
        result.Outcome.Should().Be(BackfillOutcome.Skipped);
        session.FetchOlderCalls.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies a changed UIDVALIDITY stops the backfill before it can mix UIDs of two generations.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task BackfillBatchAsync_UidValidityChanged_SkipsWithoutFetching()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (session, accountId, folderId, _) = await SeedFirstSyncAsync(db, 250, ct);
        session.UidValidity["INBOX"] = 99;

        // Act
        var result = await CreateBackfill(db).BackfillBatchAsync(session, accountId, folderId, ct);

        // Assert
        result.Outcome.Should().Be(BackfillOutcome.Skipped);
        session.FetchOlderCalls.Should().BeEmpty();
        (await db.CreateMessageRepository().GetMinUidAsync(accountId, folderId, ct)).Should().Be(231u);
    }

    /// <summary>
    /// Verifies a whole batch that vanished on the server is stepped over instead of retried forever.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task BackfillBatchAsync_WholeBatchVanishes_ContinuesWithOlderOnes()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (session, accountId, folderId, _) = await SeedFirstSyncAsync(db, 250, ct);
        foreach (var uid in Enumerable.Range(131, 100))
        {
            session.VanishingUids.Add((uint)uid);
        }

        // Act
        var result = await CreateBackfill(db).BackfillBatchAsync(session, accountId, folderId, ct);

        // Assert
        result.Outcome.Should().Be(BackfillOutcome.Progressed);
        result.Added.Should().Be(100);
        session.FetchOlderCalls.Select(c => c.BeforeUid).Should().Equal(231u, 131u);
        (await db.CreateMessageRepository().GetMinUidAsync(accountId, folderId, ct)).Should().Be(31u);
    }

    /// <summary>
    /// Verifies a server that keeps delivering nothing does not trap the backfill in a loop.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task BackfillBatchAsync_EveryBatchVanishes_StallsAfterBoundedAttempts()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (session, accountId, folderId, _) = await SeedFirstSyncAsync(db, 1000, ct);
        foreach (var uid in Enumerable.Range(1, 980))
        {
            session.VanishingUids.Add((uint)uid);
        }

        // Act
        var result = await CreateBackfill(db).BackfillBatchAsync(session, accountId, folderId, ct);

        // Assert
        result.Outcome.Should().Be(BackfillOutcome.Stalled);
        session.FetchOlderCalls.Should().HaveCount(5);
    }

    /// <summary>
    /// Verifies a failing server surfaces as an exception so the worker can back off.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task BackfillBatchAsync_ServerFails_Throws()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (session, accountId, folderId, _) = await SeedFirstSyncAsync(db, 250, ct);
        session.FailingFolders.Add("INBOX");

        // Act
        var act = () => CreateBackfill(db).BackfillBatchAsync(session, accountId, folderId, ct);

        // Assert
        await act.Should().ThrowAsync<IOException>();
        (await db.CreateMessageRepository().GetMinUidAsync(accountId, folderId, ct)).Should().Be(231u);
    }

    /// <summary>
    /// Verifies the folder order: regular folders, then All Mail, then Trash and Junk.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task GetFoldersToBackfillAsync_MixedRoles_OrdersAndFilters()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, _) = await db.SeedAccountAsync(ct);
        var folders = db.CreateFolderRepository();
        foreach (var (name, role) in new[]
        {
            ("Work", FolderRole.Other),
            ("Sent", FolderRole.Sent),
            ("Trash", FolderRole.Trash),
            ("Junk", FolderRole.Junk),
            ("All Mail", FolderRole.AllMail),
            ("Archive", FolderRole.Archive),
        })
        {
            await folders.AddAsync(MailFolder.Create(account.Id, name, name, role), ct);
        }

        // Act
        var result = await CreateBackfill(db).GetFoldersToBackfillAsync(account.Id, ct);

        // Assert
        result.Select(f => f.FullName).Should().Equal("INBOX", "Sent", "Archive", "Work", "All Mail", "Junk", "Trash");
    }

    /// <summary>
    /// Verifies All Mail is left out when the user turned it off.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task GetFoldersToBackfillAsync_AllMailDisabled_LeavesItOut()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, _) = await db.SeedAccountAsync(ct);
        await db.CreateFolderRepository().AddAsync(MailFolder.Create(account.Id, "All Mail", "All Mail", FolderRole.AllMail), ct);

        // Act
        var result = await CreateBackfill(db, new FakeSyncSettings { DownloadAllMail = false }).GetFoldersToBackfillAsync(account.Id, ct);

        // Assert
        result.Select(f => f.FullName).Should().Equal("INBOX");
    }

    /// <summary>
    /// Verifies messages the account already holds in another folder are copied locally and only unknown ones are downloaded.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task BackfillBatchAsync_MessagesKnownFromOtherFolder_AreCopiedNotDownloaded()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, _) = await db.SeedAccountAsync(ct);
        var factory = new FakeImapSessionFactory();
        factory.Session.Folders.Add(TestData.Folder("INBOX", FolderRole.Inbox));
        factory.Session.Folders.Add(TestData.Folder("All Mail", FolderRole.AllMail));
        factory.Session.Messages["INBOX"] = [.. Enumerable.Range(1, 60).Select(i => TestData.Remote((uint)i, subject: $"inbox {i}"))];

        // All Mail holds the same 60 messages (same Message-IDs, other UIDs) plus 40 archived ones.
        factory.Session.Messages["All Mail"] =
        [
            .. Enumerable.Range(1, 60).Select(i => TestData.Remote((uint)(1000 + i), messageId: $"<{i}@example.com>")),
            .. Enumerable.Range(61, 40).Select(i => TestData.Remote((uint)(1000 + i), messageId: $"<archived-{i}@example.com>")),
        ];
        await CreateSync(db, factory).SyncAsync(account.Id, ct);
        var folders = await db.CreateFolderRepository().GetByAccountAsync(account.Id, ct);
        var inbox = folders.Single(f => f.FullName == "INBOX");
        var allMail = folders.Single(f => f.FullName == "All Mail");
        var backfill = CreateBackfill(db);
        while ((await backfill.BackfillBatchAsync(factory.Session, account.Id, inbox.Id, ct)).Outcome == BackfillOutcome.Progressed)
        {
        }

        factory.Session.FetchMessagesCalls.Clear();

        // Act
        var result = await backfill.BackfillBatchAsync(factory.Session, account.Id, allMail.Id, ct);

        // Assert
        result.Outcome.Should().Be(BackfillOutcome.Completed);
        result.Added.Should().Be(80, "All Mail's first sync already cached the newest 20");
        result.Copied.Should().Be(60, "the 60 INBOX twins are copied, the 20 older archived messages are downloaded");
        factory.Session.FetchMessagesCalls.Should().OnlyContain(c => c.Uids.Length == 1, "every message is downloaded and committed on its own");
        factory.Session.FetchMessagesCalls.SelectMany(c => c.Uids).Should()
            .BeEquivalentTo(Enumerable.Range(1061, 20).Select(u => (uint)u));
        await using var verifier = db.CreateVerifier();
        var context = await verifier.AccountAsync(account.Id, ct);
        context.Messages.Count(m => m.FolderId == allMail.Id).Should().Be(100);
        context.Messages.Where(m => m.FolderId == allMail.Id).Select(m => m.Uid).Distinct().Count().Should().Be(100);
    }

    /// <summary>
    /// Verifies a download that fails midway keeps the messages already committed, and the next batch resumes below them without leaving a gap.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task BackfillBatchAsync_FailsMidBatch_KeepsCommittedMessagesAndResumesWithoutGap()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (session, accountId, folderId, _) = await SeedFirstSyncAsync(db, 250, ct);
        var backfill = CreateBackfill(db);
        session.OnFetchMessages = headers =>
        {
            if (headers.Any(h => h.Uid == 180))
            {
                throw new IOException("Simulated connection loss.");
            }
        };

        // Act
        var act = () => backfill.BackfillBatchAsync(session, accountId, folderId, ct);

        // Assert: the batch listed UIDs 131..230 and stores newest first, so 230..181 are committed when UID 180 fails.
        await act.Should().ThrowAsync<IOException>();
        var messages = db.CreateMessageRepository();
        (await messages.GetMinUidAsync(accountId, folderId, ct)).Should().Be(181u);

        session.OnFetchMessages = null;
        while ((await backfill.BackfillBatchAsync(session, accountId, folderId, ct)).Outcome == BackfillOutcome.Progressed)
        {
        }

        await using var verifier = db.CreateVerifier();
        var context = await verifier.AccountAsync(accountId, ct);
        context.Messages.Select(m => m.Uid).OrderBy(u => u).ToList().Should().Equal(Enumerable.Range(1, 250).Select(u => (uint)u));
    }

    /// <summary>
    /// Verifies the progress callback reports every committed message with the number of older messages still left in the folder.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task BackfillBatchAsync_WithCallback_ReportsEveryStoredMessage()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (session, accountId, folderId, _) = await SeedFirstSyncAsync(db, 250, ct);
        var reports = new List<(int Stored, int Left)>();

        // Act
        await CreateBackfill(db).BackfillBatchAsync(session, accountId, folderId, (stored, left) => reports.Add((stored, left)), ct);

        // Assert
        reports.Select(r => r.Stored).Should().Equal(Enumerable.Range(1, 100));
        reports[0].Left.Should().Be(229);
        reports[^1].Left.Should().Be(130);
    }

    /// <summary>
    /// Verifies a copy keeps the attachment metadata and takes the read state from the All Mail listing.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task BackfillBatchAsync_CopiedMessage_KeepsAttachmentsAndUsesListedReadState()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, inbox) = await db.SeedAccountAsync(ct);
        var allMail = MailFolder.Create(account.Id, "All Mail", "All Mail", FolderRole.AllMail);
        await db.CreateFolderRepository().AddAsync(allMail, ct);
        var messages = db.CreateMessageRepository();
        var original = MailMessage.Create(account.Id, inbox.Id, 1, "s", "A", "a@example.com", "me@example.com", DateTimeOffset.UtcNow, "body", null, "x@example.com");
        original.AddAttachment("report.pdf", "application/pdf", 321);
        var anchor = MailMessage.Create(account.Id, allMail.Id, 50, "anchor", "A", "a@example.com", "me@example.com", DateTimeOffset.UtcNow, "body", null, "anchor@example.com");
        await messages.AddRangeAsync([original, anchor], ct);
        var folder = await db.CreateFolderRepository().GetAsync(account.Id, allMail.Id, ct);
        folder!.UpdateUidValidity(1);
        await db.CreateFolderRepository().SaveChangesAsync(ct);
        var session = new FakeImapSession();
        session.Messages["All Mail"] = [TestData.Remote(7, messageId: "x@example.com") with { IsRead = true }, TestData.Remote(50)];

        // Act
        var result = await CreateBackfill(db).BackfillBatchAsync(session, account.Id, allMail.Id, ct);

        // Assert
        result.Copied.Should().Be(1);
        await using var verifier = db.CreateVerifier();
        var context = await verifier.AccountAsync(account.Id, ct);
        var copy = context.Messages.Include(m => m.Attachments).Single(m => m.FolderId == allMail.Id && m.Uid == 7);
        copy.IsRead.Should().BeTrue();
        copy.Attachments.Should().ContainSingle().Which.FileName.Should().Be("report.pdf");
        session.FetchMessagesCalls.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies the lowest cached UID is read per folder.
    /// </summary>
    /// <returns>A task that completes when the test is done.</returns>
    [Fact]
    public async Task GetMinUidAsync_EmptyAndFilledFolder_ReturnsZeroOrLowest()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, empty) = await db.SeedAccountAsync(ct);
        var (_, accountId, folderId, _) = await SeedFirstSyncAsync(db, 30, ct);
        var messages = db.CreateMessageRepository();

        // Act
        var none = await messages.GetMinUidAsync(account.Id, empty.Id, ct);
        var lowest = await messages.GetMinUidAsync(accountId, folderId, ct);

        // Assert
        none.Should().Be(0u);
        lowest.Should().Be(11u);
    }
}
