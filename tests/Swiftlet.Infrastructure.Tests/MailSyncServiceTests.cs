using AwesomeAssertions;
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
    /// <returns>The service.</returns>
    private static MailSyncService CreateService(TestDatabase db, FakeImapSessionFactory factory, int maxPerFolder = 200)
    {
        return new MailSyncService(
            new AccountRepository(db.Context),
            new FolderRepository(db.Context),
            new MessageRepository(db.Context),
            factory,
            new MailSyncOptions(maxPerFolder),
            NullLogger<MailSyncService>.Instance);
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
        var folders = await new FolderRepository(db.Context).GetByAccountAsync(account.Id, ct);
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
        var inbox = (await new FolderRepository(db.Context).GetByAccountAsync(account.Id, ct)).Single(f => f.FullName == "INBOX");
        (await new MessageRepository(db.Context).GetMaxUidAsync(inbox.Id, ct)).Should().Be(3u);
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
        var messages = new MessageRepository(db.Context);
        (await messages.SearchAsync("renumbered", account.Id, 10, ct)).Should().HaveCount(1);
        (await messages.SearchAsync("old", account.Id, 10, ct)).Should().BeEmpty();
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
        var inbox = (await new FolderRepository(db.Context).GetByAccountAsync(account.Id, ct)).Single();
        var cached = await new MessageRepository(db.Context).GetByFolderAsync(inbox.Id, 0, 10, ct);
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

        await using var verify = db.CreateContext();
        var found = await new MessageRepository(verify).SearchAsync("quarterly numbers", null, 10, ct);
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
