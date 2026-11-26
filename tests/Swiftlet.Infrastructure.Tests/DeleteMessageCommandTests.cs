using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Swiftlet.Application.Messages;
using Swiftlet.Application.Sync;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Messages;
using Swiftlet.Infrastructure.Persistence.Repositories;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="DeleteMessageCommandHandler"/> against real SQLite repositories and a scripted IMAP session.
/// </summary>
public class DeleteMessageCommandTests
{
    /// <summary>
    /// Creates the handler under test.
    /// </summary>
    /// <param name="db">The test database.</param>
    /// <param name="factory">The IMAP factory.</param>
    /// <returns>The handler.</returns>
    private static DeleteMessageCommandHandler CreateHandler(TestDatabase db, FakeImapSessionFactory factory)
    {
        return new DeleteMessageCommandHandler(
            db.CreateMessageRepository(),
            db.CreateFolderRepository(),
            db.CreateAccountRepository(),
            factory);
    }

    /// <summary>
    /// Adds a cached message to a folder.
    /// </summary>
    /// <param name="db">The test database.</param>
    /// <param name="account">The account.</param>
    /// <param name="folder">The folder.</param>
    /// <param name="uid">The UID.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The message.</returns>
    private static async Task<MailMessage> AddMessageAsync(
        TestDatabase db,
        MailAccount account,
        MailFolder folder,
        uint uid,
        CancellationToken cancellationToken)
    {
        var message = MailMessage.Create(account.Id, folder.Id, uid, "s", "A", "a@example.com", "r", DateTimeOffset.UtcNow, "b");
        await db.CreateMessageRepository().AddAsync(message, cancellationToken);
        return message;
    }

    /// <summary>
    /// Adds a Trash folder to the account.
    /// </summary>
    /// <param name="db">The test database.</param>
    /// <param name="account">The account.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The folder.</returns>
    private static async Task<MailFolder> AddTrashAsync(TestDatabase db, MailAccount account, CancellationToken cancellationToken)
    {
        var trash = MailFolder.Create(account.Id, "Trash", "Trash", FolderRole.Trash);
        await db.CreateFolderRepository().AddAsync(trash, cancellationToken);
        await db.CreateFolderRepository().SaveChangesAsync(cancellationToken);
        return trash;
    }

    /// <summary>
    /// Verifies a message outside Trash is moved to the Trash folder and its local row removed.
    /// </summary>
    [Fact]
    public async Task Delete_InboxMessage_MovesToTrashAndRemovesLocalRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, inbox) = await db.SeedAccountAsync(ct);
        var trash = await AddTrashAsync(db, account, ct);
        var message = await AddMessageAsync(db, account, inbox, 5, ct);
        var factory = new FakeImapSessionFactory();

        var outcome = await CreateHandler(db, factory).Handle(new DeleteMessageCommand(account.Id, message.Id), ct);

        outcome.Should().Be(DeleteOutcome.MovedToTrash);
        factory.Session.DeleteCalls.Should().ContainSingle().Which.Should().Be(("INBOX", 5u, (string?)trash.FullName));
        (await db.CreateMessageRepository().GetAsync(account.Id, message.Id, ct)).Should().BeNull();
        factory.Session.Disposed.Should().BeTrue();
    }

    /// <summary>
    /// Verifies a message already in Trash is deleted permanently.
    /// </summary>
    [Fact]
    public async Task Delete_MessageAlreadyInTrash_DeletesPermanently()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, _) = await db.SeedAccountAsync(ct);
        var trash = await AddTrashAsync(db, account, ct);
        var message = await AddMessageAsync(db, account, trash, 7, ct);
        var factory = new FakeImapSessionFactory();

        var outcome = await CreateHandler(db, factory).Handle(new DeleteMessageCommand(account.Id, message.Id), ct);

        outcome.Should().Be(DeleteOutcome.DeletedPermanently);
        factory.Session.DeleteCalls.Should().ContainSingle().Which.Should().Be(("Trash", 7u, (string?)null));
        (await db.CreateMessageRepository().GetAsync(account.Id, message.Id, ct)).Should().BeNull();
    }

    /// <summary>
    /// Verifies an account without a Trash folder deletes permanently.
    /// </summary>
    [Fact]
    public async Task Delete_AccountWithoutTrashFolder_DeletesPermanently()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, inbox) = await db.SeedAccountAsync(ct);
        var message = await AddMessageAsync(db, account, inbox, 5, ct);
        var factory = new FakeImapSessionFactory();

        var outcome = await CreateHandler(db, factory).Handle(new DeleteMessageCommand(account.Id, message.Id), ct);

        outcome.Should().Be(DeleteOutcome.DeletedPermanently);
        factory.Session.DeleteCalls.Should().ContainSingle().Which.Should().Be(("INBOX", 5u, (string?)null));
        (await db.CreateMessageRepository().GetAsync(account.Id, message.Id, ct)).Should().BeNull();
    }

    /// <summary>
    /// Verifies a server failure keeps the local message and propagates the error.
    /// </summary>
    [Fact]
    public async Task Delete_ServerFails_KeepsLocalMessageAndRethrows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, inbox) = await db.SeedAccountAsync(ct);
        var message = await AddMessageAsync(db, account, inbox, 5, ct);
        var factory = new FakeImapSessionFactory();
        factory.Session.DeleteFailure = new IOException("Simulated delete failure.");

        var act = async () => await CreateHandler(db, factory).Handle(new DeleteMessageCommand(account.Id, message.Id), ct);

        await act.Should().ThrowAsync<IOException>();
        (await db.CreateMessageRepository().GetAsync(account.Id, message.Id, ct)).Should().NotBeNull();
        factory.Session.Disposed.Should().BeTrue();
    }

    /// <summary>
    /// Verifies a connection failure keeps the local message.
    /// </summary>
    [Fact]
    public async Task Delete_ConnectFails_KeepsLocalMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, inbox) = await db.SeedAccountAsync(ct);
        var message = await AddMessageAsync(db, account, inbox, 5, ct);
        var factory = new FakeImapSessionFactory { Failure = new IOException("Offline.") };

        var act = async () => await CreateHandler(db, factory).Handle(new DeleteMessageCommand(account.Id, message.Id), ct);

        await act.Should().ThrowAsync<IOException>();
        (await db.CreateMessageRepository().GetAsync(account.Id, message.Id, ct)).Should().NotBeNull();
    }

    /// <summary>
    /// Verifies deleting an unknown message fails clearly.
    /// </summary>
    [Fact]
    public async Task Delete_UnknownMessage_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var factory = new FakeImapSessionFactory();

        var act = async () => await CreateHandler(db, factory).Handle(new DeleteMessageCommand(Guid.NewGuid(), Guid.NewGuid()), ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
        factory.ConnectCount.Should().Be(0);
    }

    /// <summary>
    /// Verifies a changed server UIDVALIDITY blocks the delete so a stale UID cannot hit another message.
    /// </summary>
    [Fact]
    public async Task Delete_UidValidityChangedOnServer_ThrowsAndKeepsLocalMessageAndNeverCallsDelete()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, inbox) = await db.SeedAccountAsync(ct);
        inbox.UpdateUidValidity(100);
        await db.CreateFolderRepository().SaveChangesAsync(ct);
        var message = await AddMessageAsync(db, account, inbox, 5, ct);
        var factory = new FakeImapSessionFactory();
        factory.Session.UidValidity["INBOX"] = 200;

        var act = async () => await CreateHandler(db, factory).Handle(new DeleteMessageCommand(account.Id, message.Id), ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
        factory.Session.DeleteCalls.Should().BeEmpty();
        (await db.CreateMessageRepository().GetAsync(account.Id, message.Id, ct)).Should().NotBeNull();
        factory.Session.Disposed.Should().BeTrue();
    }

    /// <summary>
    /// Verifies a matching UIDVALIDITY lets the delete proceed.
    /// </summary>
    [Fact]
    public async Task Delete_UidValidityMatches_Proceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, inbox) = await db.SeedAccountAsync(ct);
        inbox.UpdateUidValidity(100);
        await db.CreateFolderRepository().SaveChangesAsync(ct);
        var message = await AddMessageAsync(db, account, inbox, 5, ct);
        var factory = new FakeImapSessionFactory();
        factory.Session.UidValidity["INBOX"] = 100;

        await CreateHandler(db, factory).Handle(new DeleteMessageCommand(account.Id, message.Id), ct);

        factory.Session.DeleteCalls.Should().ContainSingle();
        (await db.CreateMessageRepository().GetAsync(account.Id, message.Id, ct)).Should().BeNull();
    }

    /// <summary>
    /// Verifies a folder that never recorded a UIDVALIDITY has nothing to compare and proceeds.
    /// </summary>
    [Fact]
    public async Task Delete_FolderWithoutCachedUidValidity_Proceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, inbox) = await db.SeedAccountAsync(ct);
        inbox.UidValidity.Should().BeNull();
        var message = await AddMessageAsync(db, account, inbox, 5, ct);
        var factory = new FakeImapSessionFactory();
        factory.Session.UidValidity["INBOX"] = 999;

        await CreateHandler(db, factory).Handle(new DeleteMessageCommand(account.Id, message.Id), ct);

        factory.Session.DeleteCalls.Should().ContainSingle();
    }

    /// <summary>
    /// Verifies a deleted message is not downloaded again by the next sync.
    /// </summary>
    [Fact]
    public async Task SyncAfterDelete_DoesNotRefetchDeletedMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, _) = await db.SeedAccountAsync(ct);
        var factory = new FakeImapSessionFactory();
        factory.Session.Folders.Add(TestData.Folder("INBOX", FolderRole.Inbox));
        factory.Session.Messages["INBOX"] = [TestData.Remote(1), TestData.Remote(2), TestData.Remote(3)];
        var sync = new MailSyncService(
            db.CreateAccountRepository(),
            db.CreateFolderRepository(),
            db.CreateMessageRepository(),
            factory,
            new FakeSyncSettings(),
            new MailSyncOptions(200),
            NullLogger<MailSyncService>.Instance);
        await sync.SyncAsync(account.Id, ct);
        var inbox = (await db.CreateFolderRepository().GetByAccountAsync(account.Id, ct)).Single();
        var newest = (await db.CreateMessageRepository().GetByFolderAsync(account.Id, inbox.Id, 0, 10, ct)).Single(m => m.Uid == 3);

        await CreateHandler(db, factory).Handle(new DeleteMessageCommand(account.Id, newest.Id), ct);
        var result = await sync.SyncAsync(account.Id, ct);

        result.NewMessages.Should().Be(0);
        (await db.CreateMessageRepository().ExistsAsync(account.Id, inbox.Id, 3, ct)).Should().BeFalse();
        factory.Session.Messages["INBOX"].Select(m => m.Uid).Should().Equal(1u, 2u);
    }
}
