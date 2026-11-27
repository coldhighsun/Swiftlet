using System.Text;
using AwesomeAssertions;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Queries;
using Swiftlet.Domain.Messages;
using Swiftlet.Infrastructure.Mail;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="GetAttachmentPreviewQueryHandler"/> against real SQLite repositories and a fake server.
/// </summary>
public class GetAttachmentPreviewQueryHandlerTests
{
    /// <summary>
    /// Stores a message with one attachment and creates the handler.
    /// </summary>
    /// <param name="db">The test database.</param>
    /// <param name="factory">The fake server.</param>
    /// <param name="cache">The preview cache.</param>
    /// <param name="fileName">The attachment file name.</param>
    /// <param name="contentType">The attachment content type.</param>
    /// <param name="size">The attachment size.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The handler, the query for the attachment and the account folder.</returns>
    private static async Task<(GetAttachmentPreviewQueryHandler Handler, GetAttachmentPreviewQuery Query)> ArrangeAsync(
        TestDatabase db,
        FakeImapSessionFactory factory,
        AttachmentPreviewCache cache,
        string fileName,
        string contentType,
        long size,
        CancellationToken cancellationToken)
    {
        var (account, folder) = await db.SeedAccountAsync(cancellationToken);
        var message = MailMessage.Create(
            account.Id, folder.Id, 9, "s", "A", "a@example.com", "r", DateTimeOffset.UtcNow, "b", null);
        message.AddAttachment(fileName, contentType, size);
        await db.CreateMessageRepository().AddAsync(message, cancellationToken);
        var handler = new GetAttachmentPreviewQueryHandler(
            db.CreateMessageRepository(),
            db.CreateFolderRepository(),
            db.CreateAccountRepository(),
            factory,
            new AttachmentPreviewer(),
            cache);
        return (handler, new GetAttachmentPreviewQuery(account.Id, message.Id, message.Attachments[0].Id));
    }

    /// <summary>
    /// Verifies a text attachment is downloaded and shown, and the connection is released.
    /// </summary>
    [Fact]
    public async Task Handle_TextAttachment_ReturnsTextPreview()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var factory = new FakeImapSessionFactory();
        factory.Session.AttachmentContents[("INBOX", 9, 0)] = Encoding.UTF8.GetBytes("hello");
        var (handler, query) = await ArrangeAsync(db, factory, new AttachmentPreviewCache(), "a.txt", "text/plain", 5, ct);

        var preview = await handler.Handle(query, ct);

        preview.Kind.Should().Be(AttachmentPreviewKind.Text);
        preview.Text.Should().Be("hello");
        factory.Session.Disposed.Should().BeTrue();
    }

    /// <summary>
    /// Verifies a file that cannot be previewed costs no connection.
    /// </summary>
    [Fact]
    public async Task Handle_UnsupportedType_DoesNotConnect()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var factory = new FakeImapSessionFactory();
        var (handler, query) = await ArrangeAsync(db, factory, new AttachmentPreviewCache(), "setup.exe", "application/octet-stream", 5, ct);

        var preview = await handler.Handle(query, ct);

        preview.Kind.Should().Be(AttachmentPreviewKind.Unsupported);
        factory.ConnectCount.Should().Be(0);
    }

    /// <summary>
    /// Verifies a large attachment is refused from its metadata, before any download.
    /// </summary>
    [Fact]
    public async Task Handle_TooLarge_DoesNotConnect()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var factory = new FakeImapSessionFactory();
        var (handler, query) = await ArrangeAsync(
            db, factory, new AttachmentPreviewCache(), "big.png", "image/png", GetAttachmentPreviewQueryHandler.MaxDownloadBytes + 1, ct);

        var preview = await handler.Handle(query, ct);

        preview.Kind.Should().Be(AttachmentPreviewKind.TooLarge);
        factory.ConnectCount.Should().Be(0);
    }

    /// <summary>
    /// Verifies a second request for the same attachment is answered from the cache.
    /// </summary>
    [Fact]
    public async Task Handle_SecondRequest_UsesCacheWithoutConnecting()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var factory = new FakeImapSessionFactory();
        factory.Session.AttachmentContents[("INBOX", 9, 0)] = Encoding.UTF8.GetBytes("hello");
        var (handler, query) = await ArrangeAsync(db, factory, new AttachmentPreviewCache(), "a.txt", "text/plain", 5, ct);
        await handler.Handle(query, ct);

        var second = await handler.Handle(query, ct);

        second.Text.Should().Be("hello");
        factory.ConnectCount.Should().Be(1);
    }

    /// <summary>
    /// Verifies a preview that failed is not cached, so the next hover tries again.
    /// </summary>
    [Fact]
    public async Task Handle_CorruptContent_IsNotCached()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var factory = new FakeImapSessionFactory();
        factory.Session.AttachmentContents[("INBOX", 9, 0)] = [1, 2, 3];
        var cache = new AttachmentPreviewCache();
        var (handler, query) = await ArrangeAsync(db, factory, cache, "a.png", "image/png", 3, ct);

        var preview = await handler.Handle(query, ct);

        preview.Kind.Should().Be(AttachmentPreviewKind.Unsupported);
        cache.Count.Should().Be(0);
    }

    /// <summary>
    /// Verifies an attachment that is gone from the server is reported as an error.
    /// </summary>
    [Fact]
    public async Task Handle_AttachmentGoneOnServer_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var factory = new FakeImapSessionFactory();
        var (handler, query) = await ArrangeAsync(db, factory, new AttachmentPreviewCache(), "a.txt", "text/plain", 5, ct);

        var act = async () => await handler.Handle(query, ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
