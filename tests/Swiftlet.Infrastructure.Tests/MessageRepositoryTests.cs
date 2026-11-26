using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Labels;
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
        var repository = db.CreateMessageRepository();
        var now = DateTimeOffset.UtcNow;
        var older = Build(account, folder, 1, "old", "body", now.AddHours(-2));
        var newer = Build(account, folder, 2, "new", "body", now);
        newer.AddAttachment("a.pdf", "application/pdf", 1234);
        await repository.AddAsync(older, ct);
        await repository.AddAsync(newer, ct);

        await using var verifyContext = db.CreateVerifier();
        var result = await verifyContext.Messages().GetByFolderAsync(account.Id, folder.Id, 0, 10, ct);

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
        var repository = db.CreateMessageRepository();
        await repository.AddAsync(Build(account, folder, 7, "s", "b", DateTimeOffset.UtcNow), ct);

        (await repository.ExistsAsync(account.Id, folder.Id, 7, ct)).Should().BeTrue();
        (await repository.ExistsAsync(account.Id, folder.Id, 8, ct)).Should().BeFalse();
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
        var repository = db.CreateMessageRepository();
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
        var repository = db.CreateMessageRepository();
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
        var repository = db.CreateMessageRepository();
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
        var repository = db.CreateMessageRepository();
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
        var repository = db.CreateMessageRepository();
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
        var repository = db.CreateMessageRepository();
        var message = Build(account, folder, 1, "disappearing", "text", DateTimeOffset.UtcNow);
        await repository.AddAsync(message, ct);
        (await repository.SearchAsync("disappearing", null, 10, ct)).Should().HaveCount(1);

        var accountContext = await db.AccountContextAsync(account.Id, ct);
        accountContext.Messages.Remove(message);
        await accountContext.SaveChangesAsync(ct);

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
        var repository = db.CreateMessageRepository();

        (await repository.SearchAsync("   ", null, 10, ct)).Should().BeEmpty();
    }

    /// <summary>
    /// Verifies the unified inbox merges the inboxes of all accounts, newest first.
    /// </summary>
    [Fact]
    public async Task GetUnifiedInboxAsync_TwoAccounts_ReturnsBothInboxesNewestFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account1, inbox1) = await db.SeedAccountAsync(ct);
        var (account2, inbox2) = await db.SeedAccountAsync(ct);
        var repository = db.CreateMessageRepository();
        var now = DateTimeOffset.UtcNow;
        await repository.AddAsync(Build(account1, inbox1, 1, "a-old", "b", now.AddHours(-3)), ct);
        await repository.AddAsync(Build(account2, inbox2, 1, "b-mid", "b", now.AddHours(-2)), ct);
        await repository.AddAsync(Build(account1, inbox1, 2, "a-new", "b", now.AddHours(-1)), ct);

        await using var verifyContext = db.CreateVerifier();
        var result = await verifyContext.Messages().GetUnifiedInboxAsync(0, 10, ct);

        result.Select(m => m.Subject).Should().Equal("a-new", "b-mid", "a-old");
    }

    /// <summary>
    /// Verifies folders other than the inbox are excluded from the unified inbox.
    /// </summary>
    [Fact]
    public async Task GetUnifiedInboxAsync_IgnoresNonInboxFolders()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, inbox) = await db.SeedAccountAsync(ct);
        var sent = MailFolder.Create(account.Id, "Sent", "Sent", FolderRole.Sent);
        await db.CreateFolderRepository().AddAsync(sent, ct);
        var repository = db.CreateMessageRepository();
        await repository.AddAsync(Build(account, inbox, 1, "in-inbox", "b", DateTimeOffset.UtcNow), ct);
        await repository.AddAsync(Build(account, sent, 1, "in-sent", "b", DateTimeOffset.UtcNow), ct);

        var result = await repository.GetUnifiedInboxAsync(0, 10, ct);

        result.Select(m => m.Subject).Should().Equal("in-inbox");
    }

    /// <summary>
    /// Verifies skip and take page through the unified inbox.
    /// </summary>
    [Fact]
    public async Task GetUnifiedInboxAsync_Paging_SkipAndTake()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, inbox) = await db.SeedAccountAsync(ct);
        var repository = db.CreateMessageRepository();
        var now = DateTimeOffset.UtcNow;
        for (var i = 1u; i <= 5; i++)
        {
            await repository.AddAsync(Build(account, inbox, i, $"m{i}", "b", now.AddMinutes(i)), ct);
        }

        var result = await repository.GetUnifiedInboxAsync(1, 2, ct);

        result.Select(m => m.Subject).Should().Equal("m4", "m3");
    }

    /// <summary>
    /// Verifies the label query spans accounts and ignores unlabelled messages.
    /// </summary>
    [Fact]
    public async Task GetByLabelAsync_ReturnsOnlyLabelledMessagesAcrossAccounts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account1, inbox1) = await db.SeedAccountAsync(ct);
        var (account2, inbox2) = await db.SeedAccountAsync(ct);
        var label = Label.Create("Work", "#112233");
        db.Context.Labels.Add(label);
        await db.Context.SaveChangesAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var first = Build(account1, inbox1, 1, "first", "b", now.AddHours(-1));
        var second = Build(account2, inbox2, 1, "second", "b", now);
        var unlabelled = Build(account1, inbox1, 2, "plain", "b", now);
        first.AddLabel(label);
        second.AddLabel(label);
        var repository = db.CreateMessageRepository();
        await repository.AddRangeAsync([first, second, unlabelled], ct);
        await repository.SaveLabelsAsync(first, ct);
        await repository.SaveLabelsAsync(second, ct);

        await using var verifyContext = db.CreateVerifier();
        var result = await verifyContext.Messages().GetByLabelAsync(label.Id, 0, 10, ct);

        result.Select(m => m.Subject).Should().Equal("second", "first");
        result[0].Labels.Should().ContainSingle().Which.Name.Should().Be("Work");
    }

    /// <summary>
    /// Verifies deleting a message removes its attachments and label links but keeps the label.
    /// </summary>
    [Fact]
    public async Task DeleteAsync_RemovesMessageAttachmentsAndLabelLinks()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await db.SeedAccountAsync(ct);
        var label = Label.Create("Work", "#112233");
        db.Context.Labels.Add(label);
        await db.Context.SaveChangesAsync(ct);
        var message = Build(account, folder, 1, "s", "b", DateTimeOffset.UtcNow);
        message.AddAttachment("a.pdf", "application/pdf", 10);
        message.AddLabel(label);
        var repository = db.CreateMessageRepository();
        await repository.AddAsync(message, ct);
        await repository.SaveLabelsAsync(message, ct);

        await repository.DeleteAsync(account.Id, message.Id, ct);

        await using var verifyContext = db.CreateVerifier();
        var accountContext = await verifyContext.AccountAsync(account.Id, ct);
        accountContext.Messages.Should().BeEmpty();
        accountContext.Attachments.Should().BeEmpty();
        verifyContext.Global.Labels.Should().ContainSingle();
        var links = await verifyContext.Global.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM MessageLabels")
            .SingleAsync(ct);
        links.Should().Be(0);
    }

    /// <summary>
    /// Verifies deleting an unknown message does nothing.
    /// </summary>
    [Fact]
    public async Task DeleteAsync_Unknown_IsNoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await db.SeedAccountAsync(ct);
        var repository = db.CreateMessageRepository();
        await repository.AddAsync(Build(account, folder, 1, "keep", "b", DateTimeOffset.UtcNow), ct);

        await repository.DeleteAsync(account.Id, Guid.NewGuid(), ct);

        (await repository.GetByFolderAsync(account.Id, folder.Id, 0, 10, ct)).Should().ContainSingle();
    }

    /// <summary>
    /// Verifies deleting a message also removes it from the full-text index.
    /// </summary>
    [Fact]
    public async Task SearchAsync_AfterDelete_NoLongerMatches()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await db.SeedAccountAsync(ct);
        var repository = db.CreateMessageRepository();
        var message = Build(account, folder, 1, "vanishing", "text", DateTimeOffset.UtcNow);
        await repository.AddAsync(message, ct);
        (await repository.SearchAsync("vanishing", null, 10, ct)).Should().HaveCount(1);

        await repository.DeleteAsync(account.Id, message.Id, ct);

        (await repository.SearchAsync("vanishing", null, 10, ct)).Should().BeEmpty();
    }

    /// <summary>
    /// Verifies search results carry their labels.
    /// </summary>
    [Fact]
    public async Task SearchAsync_LoadsLabels()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await db.SeedAccountAsync(ct);
        var label = Label.Create("Work", "#112233");
        db.Context.Labels.Add(label);
        await db.Context.SaveChangesAsync(ct);
        var message = Build(account, folder, 1, "labelled subject", "text", DateTimeOffset.UtcNow);
        message.AddLabel(label);
        var repository = db.CreateMessageRepository();
        await repository.AddAsync(message, ct);
        await repository.SaveLabelsAsync(message, ct);

        await using var verifyContext = db.CreateVerifier();
        var result = await verifyContext.Messages().SearchAsync("labelled", null, 10, ct);

        result.Should().ContainSingle().Which.Labels.Should().ContainSingle();
    }

    /// <summary>
    /// Verifies only pending messages of the requested folder are returned, untracked and ordered by UID.
    /// </summary>
    [Fact]
    public async Task GetPendingFlagStatesAsync_ReturnsOnlyPendingOfThatFolder()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await db.SeedAccountAsync(ct);
        var other = MailFolder.Create(account.Id, "Archive", "Archive", FolderRole.Archive);
        await db.CreateFolderRepository().AddAsync(other, ct);
        var repository = db.CreateMessageRepository();
        var pending = Build(account, folder, 1, "pending", "b", DateTimeOffset.UtcNow);
        var clean = Build(account, folder, 2, "clean", "b", DateTimeOffset.UtcNow);
        var otherFolder = Build(account, other, 1, "other", "b", DateTimeOffset.UtcNow);
        await repository.AddRangeAsync([pending, clean, otherFolder], ct);
        pending.SetRead(true);
        otherFolder.SetRead(true);
        await repository.SaveChangesAsync(ct);
        await using var verify = db.CreateVerifier();

        var result = await verify.Messages().GetPendingFlagStatesAsync(account.Id, folder.Id, ct);

        result.Should().ContainSingle().Which.Should().Be(new MessageFlagState(pending.Id, 1, true, false, true));
        (await verify.AccountAsync(account.Id, ct)).ChangeTracker.Entries().Should().BeEmpty();
    }

    /// <summary>
    /// Verifies the flag state query returns every message of the folder ordered by UID without tracking them.
    /// </summary>
    [Fact]
    public async Task GetFlagStatesAsync_ReturnsFolderMessagesOrderedByUidUntracked()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await db.SeedAccountAsync(ct);
        var other = MailFolder.Create(account.Id, "Archive", "Archive", FolderRole.Archive);
        await db.CreateFolderRepository().AddAsync(other, ct);
        var repository = db.CreateMessageRepository();
        var nine = Build(account, folder, 9, "nine", "b", DateTimeOffset.UtcNow);
        nine.SetFlagged(true);
        await repository.AddRangeAsync(
            [
                Build(account, folder, 5, "five", "b", DateTimeOffset.UtcNow),
                Build(account, folder, 2, "two", "b", DateTimeOffset.UtcNow),
                nine,
                Build(account, other, 1, "elsewhere", "b", DateTimeOffset.UtcNow),
            ],
            ct);
        await using var verify = db.CreateVerifier();

        var result = await verify.Messages().GetFlagStatesAsync(account.Id, folder.Id, ct);

        result.Select(m => m.Uid).Should().Equal(2u, 5u, 9u);
        result[2].Should().Be(new MessageFlagState(nine.Id, 9, false, true, true));
        (await verify.AccountAsync(account.Id, ct)).ChangeTracker.Entries().Should().BeEmpty();
    }

    /// <summary>
    /// Verifies applying server flags updates clean rows and skips rows with a pending local change.
    /// </summary>
    [Fact]
    public async Task ApplyServerFlagsAsync_UpdatesCleanRowAndSkipsPendingRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await db.SeedAccountAsync(ct);
        var repository = db.CreateMessageRepository();
        var clean = Build(account, folder, 1, "clean", "b", DateTimeOffset.UtcNow);
        var pending = Build(account, folder, 2, "pending", "b", DateTimeOffset.UtcNow);
        await repository.AddRangeAsync([clean, pending], ct);
        pending.SetRead(true);
        await repository.SaveChangesAsync(ct);

        var cleanRows = await repository.ApplyServerFlagsAsync(account.Id, clean.Id, true, true, ct);
        var pendingRows = await repository.ApplyServerFlagsAsync(account.Id, pending.Id, false, true, ct);

        cleanRows.Should().Be(1);
        pendingRows.Should().Be(0);
        await using var verify = db.CreateVerifier();
        var states = await verify.Messages().GetFlagStatesAsync(account.Id, folder.Id, ct);
        states[0].Should().Be(new MessageFlagState(clean.Id, 1, true, true, false));
        states[1].Should().Be(new MessageFlagState(pending.Id, 2, true, false, true));
    }

    /// <summary>
    /// Verifies the pending marker is cleared only for rows whose values still equal the pushed ones.
    /// </summary>
    [Fact]
    public async Task ClearFlagSyncPendingAsync_ClearsOnlyRowsWhoseValuesStillMatch()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await db.SeedAccountAsync(ct);
        var repository = db.CreateMessageRepository();
        var same = Build(account, folder, 1, "same", "b", DateTimeOffset.UtcNow);
        var changed = Build(account, folder, 2, "changed", "b", DateTimeOffset.UtcNow);
        await repository.AddRangeAsync([same, changed], ct);
        same.SetRead(true);
        changed.SetRead(true);
        await repository.SaveChangesAsync(ct);
        var pushed = await repository.GetPendingFlagStatesAsync(account.Id, folder.Id, ct);
        // The user stars the second message after the states were loaded.
        await using (var other = db.CreateVerifier())
        {
            var otherContext = await other.AccountAsync(account.Id, ct);
            var loaded = await otherContext.Messages.SingleAsync(m => m.Id == changed.Id, ct);
            loaded.SetFlagged(true);
            await otherContext.SaveChangesAsync(ct);
        }

        var cleared = await repository.ClearFlagSyncPendingAsync(account.Id, pushed, ct);

        cleared.Should().Be(1);
        await using var verify = db.CreateVerifier();
        var states = await verify.Messages().GetFlagStatesAsync(account.Id, folder.Id, ct);
        states[0].IsFlagSyncPending.Should().BeFalse();
        states[1].Should().Be(new MessageFlagState(changed.Id, 2, true, true, true));
    }

    /// <summary>
    /// Verifies clearing with no pushed states does nothing.
    /// </summary>
    [Fact]
    public async Task ClearFlagSyncPendingAsync_Empty_ReturnsZero()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);

        var cleared = await db.CreateMessageRepository().ClearFlagSyncPendingAsync(Guid.NewGuid(), [], ct);

        cleared.Should().Be(0);
    }

    /// <summary>
    /// Verifies a range delete removes messages, attachments and label links but keeps other messages and labels.
    /// </summary>
    [Fact]
    public async Task DeleteRangeAsync_RemovesMessagesAttachmentsAndLabelLinks()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await db.SeedAccountAsync(ct);
        var label = Label.Create("Work", "#112233");
        db.Context.Labels.Add(label);
        await db.Context.SaveChangesAsync(ct);
        var first = Build(account, folder, 1, "first", "b", DateTimeOffset.UtcNow);
        var second = Build(account, folder, 2, "second", "b", DateTimeOffset.UtcNow);
        var keep = Build(account, folder, 3, "keepme", "b", DateTimeOffset.UtcNow);
        first.AddAttachment("a.pdf", "application/pdf", 10);
        second.AddLabel(label);
        keep.AddAttachment("k.pdf", "application/pdf", 10);
        var repository = db.CreateMessageRepository();
        await repository.AddRangeAsync([first, second, keep], ct);
        await repository.SaveLabelsAsync(second, ct);

        await repository.DeleteRangeAsync(account.Id, [first.Id, second.Id], ct);

        await using var verifyContext = db.CreateVerifier();
        var accountContext = await verifyContext.AccountAsync(account.Id, ct);
        accountContext.Messages.Should().ContainSingle().Which.Subject.Should().Be("keepme");
        accountContext.Attachments.Should().ContainSingle().Which.FileName.Should().Be("k.pdf");
        verifyContext.Global.Labels.Should().ContainSingle();
        var links = await verifyContext.Global.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM MessageLabels")
            .SingleAsync(ct);
        links.Should().Be(0);
        (await repository.SearchAsync("second", null, 10, ct)).Should().BeEmpty();
    }

    /// <summary>
    /// Verifies a range delete with no ids does nothing.
    /// </summary>
    [Fact]
    public async Task DeleteRangeAsync_Empty_IsNoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await db.SeedAccountAsync(ct);
        var repository = db.CreateMessageRepository();
        await repository.AddAsync(Build(account, folder, 1, "keep", "b", DateTimeOffset.UtcNow), ct);

        await repository.DeleteRangeAsync(account.Id, [], ct);

        (await repository.GetByFolderAsync(account.Id, folder.Id, 0, 10, ct)).Should().ContainSingle();
    }

    /// <summary>
    /// Verifies deleting more ids than one batch removes every one of them.
    /// </summary>
    [Fact]
    public async Task DeleteRangeAsync_MoreThanOneBatch_DeletesAll()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await db.SeedAccountAsync(ct);
        var repository = db.CreateMessageRepository();
        var messages = Enumerable.Range(1, 1100)
            .Select(i => Build(account, folder, (uint)i, $"m{i}", "b", DateTimeOffset.UtcNow))
            .ToList();
        await repository.AddRangeAsync(messages, ct);

        await repository.DeleteRangeAsync(account.Id, messages.Select(m => m.Id).ToList(), ct);

        await using var verifyContext = db.CreateVerifier();
        (await verifyContext.AccountAsync(account.Id, ct)).Messages.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies the pending marker is persisted and cleared.
    /// </summary>
    [Fact]
    public async Task PendingMarker_RoundTripsThroughDatabase()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await db.SeedAccountAsync(ct);
        var repository = db.CreateMessageRepository();
        var message = Build(account, folder, 1, "s", "b", DateTimeOffset.UtcNow);
        await repository.AddAsync(message, ct);
        message.SetFlagged(true);
        await repository.SaveChangesAsync(ct);

        await using (var verifyContext = db.CreateVerifier())
        {
            var loaded = await verifyContext.Messages().GetAsync(account.Id, message.Id, ct);
            loaded!.IsFlagSyncPending.Should().BeTrue();
            loaded.MarkFlagsSynced();
            await verifyContext.Messages().SaveChangesAsync(ct);
        }

        await using var finalContext = db.CreateVerifier();
        (await finalContext.Messages().GetAsync(account.Id, message.Id, ct))!.IsFlagSyncPending.Should().BeFalse();
    }

    /// <summary>
    /// Verifies skip and take cut the page from the merged, newest-first list of several accounts.
    /// </summary>
    [Fact]
    public async Task GetUnifiedInboxAsync_ThreeAccounts_PagesThroughMergedList()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var repository = db.CreateMessageRepository();
        var now = DateTimeOffset.UtcNow;
        for (var a = 0; a < 3; a++)
        {
            var (account, inbox) = await db.SeedAccountAsync(ct);
            for (var i = 1u; i <= 3; i++)
            {
                // The times interleave the accounts: m1..m9 get ever newer times and each account holds every third one.
                await repository.AddAsync(Build(account, inbox, i, $"m{(i - 1) * 3 + a + 1}", "b", now.AddMinutes((i - 1) * 3 + a)), ct);
            }
        }

        await using var verify = db.CreateVerifier();
        var firstPage = await verify.Messages().GetUnifiedInboxAsync(0, 4, ct);
        var secondPage = await verify.Messages().GetUnifiedInboxAsync(4, 4, ct);
        var lastPage = await verify.Messages().GetUnifiedInboxAsync(8, 4, ct);

        firstPage.Select(m => m.Subject).Should().Equal("m9", "m8", "m7", "m6");
        secondPage.Select(m => m.Subject).Should().Equal("m5", "m4", "m3", "m2");
        lastPage.Select(m => m.Subject).Should().Equal("m1");
    }

    /// <summary>
    /// Verifies the label query merges and pages the labelled messages of several accounts newest first.
    /// </summary>
    [Fact]
    public async Task GetByLabelAsync_TwoAccounts_PagesThroughMergedList()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account1, inbox1) = await db.SeedAccountAsync(ct);
        var (account2, inbox2) = await db.SeedAccountAsync(ct);
        var label = Label.Create("Work", "#112233");
        db.Context.Labels.Add(label);
        await db.Context.SaveChangesAsync(ct);
        var repository = db.CreateMessageRepository();
        var now = DateTimeOffset.UtcNow;
        for (var i = 1u; i <= 4; i++)
        {
            var account = i % 2 == 0 ? account2 : account1;
            var inbox = i % 2 == 0 ? inbox2 : inbox1;
            var message = Build(account, inbox, i, $"l{i}", "b", now.AddMinutes(i));
            message.AddLabel(label);
            await repository.AddAsync(message, ct);
            await repository.SaveLabelsAsync(message, ct);
        }

        await repository.AddAsync(Build(account1, inbox1, 99, "unlabelled", "b", now.AddMinutes(50)), ct);

        await using var verify = db.CreateVerifier();
        var page = await verify.Messages().GetByLabelAsync(label.Id, 1, 2, ct);

        page.Select(m => m.Subject).Should().Equal("l3", "l2");
        page.Select(m => m.AccountId).Should().Equal(account1.Id, account2.Id);
    }

    /// <summary>
    /// Verifies two accounts keep their messages in separate databases.
    /// </summary>
    [Fact]
    public async Task AddAsync_TwoAccounts_StoresEachMessageInItsOwnDatabase()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account1, inbox1) = await db.SeedAccountAsync(ct);
        var (account2, inbox2) = await db.SeedAccountAsync(ct);
        var repository = db.CreateMessageRepository();
        await repository.AddAsync(Build(account1, inbox1, 1, "one", "b", DateTimeOffset.UtcNow), ct);
        await repository.AddAsync(Build(account2, inbox2, 1, "two", "b", DateTimeOffset.UtcNow), ct);

        await using var verify = db.CreateVerifier();

        (await verify.AccountAsync(account1.Id, ct)).Messages.Select(m => m.Subject).Should().Equal("one");
        (await verify.AccountAsync(account2.Id, ct)).Messages.Select(m => m.Subject).Should().Equal("two");
        db.Factory.OpenAccounts.Should().BeEquivalentTo([account1.Id, account2.Id]);
    }
}
