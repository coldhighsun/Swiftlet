using AwesomeAssertions;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Queries;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="GetInlineImagesQueryHandler"/> against real SQLite repositories and a fake server.
/// </summary>
public class GetInlineImagesQueryHandlerTests
{
    /// <summary>
    /// Verifies the server is not contacted when the HTML has no cid references.
    /// </summary>
    [Fact]
    public async Task Handle_NoCidReferences_ReturnsEmptyWithoutConnecting()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await db.SeedAccountAsync(ct);
        var message = MailMessage.Create(
            account.Id, folder.Id, 5, "s", "A", "a@example.com", "r", DateTimeOffset.UtcNow, "b", "<img src=\"https://x.example/a.png\">");
        await db.CreateMessageRepository().AddAsync(message, ct);
        var factory = new FakeImapSessionFactory();
        var handler = new GetInlineImagesQueryHandler(
            db.CreateMessageRepository(), db.CreateFolderRepository(), db.CreateAccountRepository(), factory);

        var result = await handler.Handle(new GetInlineImagesQuery(account.Id, message.Id), ct);

        result.Should().BeEmpty();
        factory.ConnectCount.Should().Be(0);
    }

    /// <summary>
    /// Verifies the referenced identifiers are requested from the server and its images returned.
    /// </summary>
    [Fact]
    public async Task Handle_CidReferences_ReturnsImagesFromServer()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await db.SeedAccountAsync(ct);
        var message = MailMessage.Create(
            account.Id, folder.Id, 6, "s", "A", "a@example.com", "r", DateTimeOffset.UtcNow, "b", "<img src=\"cid:Logo\"><img src=\"cid:sig\">");
        await db.CreateMessageRepository().AddAsync(message, ct);
        var factory = new FakeImapSessionFactory();
        var image = new InlineImage("image/png", new byte[] { 1, 2 });
        factory.Session.InlineImages[("INBOX", 6)] = new Dictionary<string, InlineImage> { ["logo"] = image };
        var handler = new GetInlineImagesQueryHandler(
            db.CreateMessageRepository(), db.CreateFolderRepository(), db.CreateAccountRepository(), factory);

        var result = await handler.Handle(new GetInlineImagesQuery(account.Id, message.Id), ct);

        result.Should().ContainSingle().Which.Value.Should().Be(image);
        factory.Session.InlineImageCalls.Should().ContainSingle().Which.ContentIds.Should().BeEquivalentTo(["logo", "sig"]);
        factory.Session.Disposed.Should().BeTrue();
    }

    /// <summary>
    /// Verifies a message that is gone from the server yields no images instead of an error.
    /// </summary>
    [Fact]
    public async Task Handle_MessageGoneOnServer_ReturnsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await db.SeedAccountAsync(ct);
        var message = MailMessage.Create(
            account.Id, folder.Id, 7, "s", "A", "a@example.com", "r", DateTimeOffset.UtcNow, "b", "<img src=\"cid:logo\">");
        await db.CreateMessageRepository().AddAsync(message, ct);
        var factory = new FakeImapSessionFactory();
        var handler = new GetInlineImagesQueryHandler(
            db.CreateMessageRepository(), db.CreateFolderRepository(), db.CreateAccountRepository(), factory);

        var result = await handler.Handle(new GetInlineImagesQuery(account.Id, message.Id), ct);

        result.Should().BeEmpty();
        factory.Session.InlineImageCalls.Should().ContainSingle();
    }

    /// <summary>
    /// Verifies an unknown message is reported as an error.
    /// </summary>
    [Fact]
    public async Task Handle_UnknownMessage_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, _) = await db.SeedAccountAsync(ct);
        var handler = new GetInlineImagesQueryHandler(
            db.CreateMessageRepository(), db.CreateFolderRepository(), db.CreateAccountRepository(), new FakeImapSessionFactory());

        var act = async () => await handler.Handle(new GetInlineImagesQuery(account.Id, Guid.NewGuid()), ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
