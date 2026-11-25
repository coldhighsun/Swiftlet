using AwesomeAssertions;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Messages;
using Swiftlet.Application.Queries;
using Swiftlet.Domain.Messages;
using Swiftlet.Infrastructure.Persistence.Repositories;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for the read-side queries and message state commands used by the UI.
/// </summary>
public class QueryAndMessageCommandTests
{
    /// <summary>
    /// Verifies attachments keep their server-side ordinal through persistence.
    /// </summary>
    [Fact]
    public async Task GetAsync_MessageWithAttachments_PreservesOrdinals()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await db.SeedAccountAsync(ct);
        var repository = new MessageRepository(db.Context);
        var message = MailMessage.Create(account.Id, folder.Id, 1, "s", "A", "a@example.com", "r", DateTimeOffset.UtcNow, "b");
        message.AddAttachment("first.txt", "text/plain", 1);
        message.AddAttachment("second.txt", "text/plain", 2);
        await repository.AddAsync(message, ct);

        await using var verify = db.CreateContext();
        var loaded = await new MessageRepository(verify).GetAsync(message.Id, ct);

        loaded!.Attachments.OrderBy(a => a.Ordinal).Select(a => (a.Ordinal, a.FileName))
            .Should().Equal((0, "first.txt"), (1, "second.txt"));
    }

    /// <summary>
    /// Verifies the read and flagged commands persist their state.
    /// </summary>
    [Fact]
    public async Task SetReadAndFlaggedCommands_Handled_PersistState()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await db.SeedAccountAsync(ct);
        var repository = new MessageRepository(db.Context);
        var message = MailMessage.Create(account.Id, folder.Id, 1, "s", "A", "a@example.com", "r", DateTimeOffset.UtcNow, "b");
        await repository.AddAsync(message, ct);

        await new SetMessageReadCommandHandler(repository).Handle(new SetMessageReadCommand(message.Id, true), ct);
        await new SetMessageFlaggedCommandHandler(repository).Handle(new SetMessageFlaggedCommand(message.Id, true), ct);

        await using var verify = db.CreateContext();
        var loaded = await new MessageRepository(verify).GetAsync(message.Id, ct);
        loaded!.IsRead.Should().BeTrue();
        loaded.IsFlagged.Should().BeTrue();
    }

    /// <summary>
    /// Verifies state commands for unknown messages fail clearly.
    /// </summary>
    [Fact]
    public async Task SetReadCommand_UnknownMessage_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var handler = new SetMessageReadCommandHandler(new MessageRepository(db.Context));

        var act = async () => await handler.Handle(new SetMessageReadCommand(Guid.NewGuid(), true), ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>
    /// Verifies the list and search queries return cached messages.
    /// </summary>
    [Fact]
    public async Task MessageQueries_Handled_ReturnFolderPageAndSearchResults()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await db.SeedAccountAsync(ct);
        var repository = new MessageRepository(db.Context);
        var now = DateTimeOffset.UtcNow;
        await repository.AddAsync(MailMessage.Create(account.Id, folder.Id, 1, "alpha topic", "A", "a@example.com", "r", now.AddMinutes(-1), "b"), ct);
        await repository.AddAsync(MailMessage.Create(account.Id, folder.Id, 2, "beta topic", "A", "a@example.com", "r", now, "b"), ct);

        var page = await new GetMessagesQueryHandler(repository).Handle(new GetMessagesQuery(folder.Id, 0, 10), ct);
        var found = await new SearchMessagesQueryHandler(repository).Handle(new SearchMessagesQuery("alpha", account.Id, 10), ct);
        var folders = await new GetFoldersQueryHandler(new FolderRepository(db.Context)).Handle(new GetFoldersQuery(account.Id), ct);
        var accounts = await new GetAccountsQueryHandler(new AccountRepository(db.Context)).Handle(new GetAccountsQuery(), ct);

        page.Select(m => m.Subject).Should().Equal("beta topic", "alpha topic");
        found.Should().ContainSingle().Which.Subject.Should().Be("alpha topic");
        folders.Should().ContainSingle();
        accounts.Should().ContainSingle();
    }

    /// <summary>
    /// Verifies an attachment is downloaded by folder, UID and ordinal and returned with its metadata.
    /// </summary>
    [Fact]
    public async Task DownloadAttachment_ExistingAttachment_ReturnsContent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await db.SeedAccountAsync(ct);
        var repository = new MessageRepository(db.Context);
        var message = MailMessage.Create(account.Id, folder.Id, 9, "s", "A", "a@example.com", "r", DateTimeOffset.UtcNow, "b");
        message.AddAttachment("a.txt", "text/plain", 1);
        message.AddAttachment("b.txt", "text/plain", 3);
        await repository.AddAsync(message, ct);
        var factory = new FakeImapSessionFactory();
        factory.Session.AttachmentContents[("INBOX", 9, 1)] = [1, 2, 3];
        var handler = new DownloadAttachmentQueryHandler(
            repository, new FolderRepository(db.Context), new AccountRepository(db.Context), factory);
        var second = message.Attachments.Single(a => a.Ordinal == 1);

        var result = await handler.Handle(new DownloadAttachmentQuery(message.Id, second.Id), ct);

        result.FileName.Should().Be("b.txt");
        result.Content.ToArray().Should().Equal(1, 2, 3);
        factory.Session.Disposed.Should().BeTrue();
    }

    /// <summary>
    /// Verifies a missing server-side attachment is reported as an error.
    /// </summary>
    [Fact]
    public async Task DownloadAttachment_MissingOnServer_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await db.SeedAccountAsync(ct);
        var repository = new MessageRepository(db.Context);
        var message = MailMessage.Create(account.Id, folder.Id, 9, "s", "A", "a@example.com", "r", DateTimeOffset.UtcNow, "b");
        message.AddAttachment("a.txt", "text/plain", 1);
        await repository.AddAsync(message, ct);
        var handler = new DownloadAttachmentQueryHandler(
            repository, new FolderRepository(db.Context), new AccountRepository(db.Context), new FakeImapSessionFactory());

        var act = async () => await handler.Handle(new DownloadAttachmentQuery(message.Id, message.Attachments[0].Id), ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
