using AwesomeAssertions;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Messages;
using Swiftlet.Infrastructure.Persistence.Repositories;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="MessageRepository"/>, including FTS5 search.
/// </summary>
public class MessageRepositoryTests
{
    /// <summary>
    /// Builds a message with sensible defaults.
    /// </summary>
    /// <param name="account">The owning account.</param>
    /// <param name="folder">The containing folder.</param>
    /// <param name="uid">The IMAP UID.</param>
    /// <param name="subject">The subject.</param>
    /// <param name="body">The plain-text body.</param>
    /// <param name="sentAt">The sent time.</param>
    /// <param name="from">The sender address.</param>
    /// <returns>The message.</returns>
    private static MailMessage Build(
        MailAccount account,
        MailFolder folder,
        uint uid,
        string subject,
        string body,
        DateTimeOffset sentAt,
        string from = "alice@example.com")
    {
        return MailMessage.Create(account.Id, folder.Id, uid, subject, "Alice", from, "me@example.com", sentAt, body);
    }

    /// <summary>
    /// Verifies a saved message with attachments round-trips and orders newest first.
    /// </summary>
    [Fact]
    public async Task GetByFolderAsync_MultipleMessages_ReturnsNewestFirstWithAttachments()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await db.SeedAccountAsync(ct);
        var repository = new MessageRepository(db.Context);
        var now = DateTimeOffset.UtcNow;
        var older = Build(account, folder, 1, "old", "body", now.AddHours(-2));
        var newer = Build(account, folder, 2, "new", "body", now);
        newer.AddAttachment("a.pdf", "application/pdf", 1234);
        await repository.AddAsync(older, ct);
        await repository.AddAsync(newer, ct);

        await using var verifyContext = db.CreateContext();
        var result = await new MessageRepository(verifyContext).GetByFolderAsync(folder.Id, 0, 10, ct);

        result.Select(m => m.Subject).Should().Equal("new", "old");
        result[0].Attachments.Should().ContainSingle().Which.FileName.Should().Be("a.pdf");
        result[0].SentAt.Should().BeCloseTo(now, TimeSpan.FromMilliseconds(1));
    }

    /// <summary>
    /// Verifies the unique (folder, UID) constraint is observable through <c>ExistsAsync</c>.
    /// </summary>
    [Fact]
    public async Task ExistsAsync_KnownAndUnknownUid_ReturnsExpected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await db.SeedAccountAsync(ct);
        var repository = new MessageRepository(db.Context);
        await repository.AddAsync(Build(account, folder, 7, "s", "b", DateTimeOffset.UtcNow), ct);

        (await repository.ExistsAsync(folder.Id, 7, ct)).Should().BeTrue();
        (await repository.ExistsAsync(folder.Id, 8, ct)).Should().BeFalse();
    }

    /// <summary>
    /// Verifies a multi-term query requires every term to match.
    /// </summary>
    [Fact]
    public async Task SearchAsync_MultipleTerms_RequiresAllTerms()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await db.SeedAccountAsync(ct);
        var repository = new MessageRepository(db.Context);
        await repository.AddAsync(Build(account, folder, 1, "Quarterly report", "revenue grew", DateTimeOffset.UtcNow), ct);
        await repository.AddAsync(Build(account, folder, 2, "Lunch plans", "revenue lunch", DateTimeOffset.UtcNow), ct);

        var result = await repository.SearchAsync("report revenue", null, 10, ct);

        result.Should().ContainSingle().Which.Subject.Should().Be("Quarterly report");
    }

    /// <summary>
    /// Verifies Chinese text is searchable (trigram tokenizer).
    /// </summary>
    [Fact]
    public async Task SearchAsync_ChineseSubstring_FindsMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await db.SeedAccountAsync(ct);
        var repository = new MessageRepository(db.Context);
        await repository.AddAsync(Build(account, folder, 1, "项目周报", "本周完成了邮件同步模块", DateTimeOffset.UtcNow), ct);

        (await repository.SearchAsync("同步模块", null, 10, ct)).Should().HaveCount(1);
        (await repository.SearchAsync("周报", null, 10, ct)).Should().HaveCount(1);
    }

    /// <summary>
    /// Verifies the account filter restricts results.
    /// </summary>
    [Fact]
    public async Task SearchAsync_AccountFilter_ExcludesOtherAccounts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account1, folder1) = await db.SeedAccountAsync(ct);
        var (account2, folder2) = await db.SeedAccountAsync(ct);
        var repository = new MessageRepository(db.Context);
        await repository.AddAsync(Build(account1, folder1, 1, "invoice", "text", DateTimeOffset.UtcNow), ct);
        await repository.AddAsync(Build(account2, folder2, 1, "invoice", "text", DateTimeOffset.UtcNow), ct);

        (await repository.SearchAsync("invoice", null, 10, ct)).Should().HaveCount(2);
        (await repository.SearchAsync("invoice", account1.Id, 10, ct)).Should().ContainSingle()
            .Which.AccountId.Should().Be(account1.Id);
    }

    /// <summary>
    /// Verifies a query containing quotes is treated as literal text instead of FTS syntax.
    /// </summary>
    [Fact]
    public async Task SearchAsync_QueryWithFtsSyntaxCharacters_DoesNotThrow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await db.SeedAccountAsync(ct);
        var repository = new MessageRepository(db.Context);
        await repository.AddAsync(Build(account, folder, 1, "hello world", "text", DateTimeOffset.UtcNow), ct);

        var result = await repository.SearchAsync("\"hello* OR (world", null, 10, ct);

        result.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies short terms fall back to LIKE and wildcards are escaped.
    /// </summary>
    [Fact]
    public async Task SearchAsync_ShortTerm_UsesLikeFallback()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await db.SeedAccountAsync(ct);
        var repository = new MessageRepository(db.Context);
        await repository.AddAsync(Build(account, folder, 1, "周报", "100% done", DateTimeOffset.UtcNow), ct);
        await repository.AddAsync(Build(account, folder, 2, "other", "nothing", DateTimeOffset.UtcNow), ct);

        (await repository.SearchAsync("周报", null, 10, ct)).Should().ContainSingle();
        (await repository.SearchAsync("%", null, 10, ct)).Should().ContainSingle()
            .Which.BodyText.Should().Contain("100%");
    }

    /// <summary>
    /// Verifies the FTS index follows updates and deletes through triggers.
    /// </summary>
    [Fact]
    public async Task SearchAsync_AfterDelete_NoLongerFindsMessage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await db.SeedAccountAsync(ct);
        var repository = new MessageRepository(db.Context);
        var message = Build(account, folder, 1, "disappearing", "text", DateTimeOffset.UtcNow);
        await repository.AddAsync(message, ct);
        (await repository.SearchAsync("disappearing", null, 10, ct)).Should().HaveCount(1);

        db.Context.Messages.Remove(message);
        await db.Context.SaveChangesAsync(ct);

        (await repository.SearchAsync("disappearing", null, 10, ct)).Should().BeEmpty();
    }

    /// <summary>
    /// Verifies a blank query returns nothing.
    /// </summary>
    [Fact]
    public async Task SearchAsync_BlankQuery_ReturnsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var repository = new MessageRepository(db.Context);

        (await repository.SearchAsync("   ", null, 10, ct)).Should().BeEmpty();
    }
}
