using AwesomeAssertions;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Domain.Tests;

/// <summary>
/// Tests for <see cref="MailMessage"/> and <see cref="MailFolder"/>.
/// </summary>
public class MailMessageAndFolderTests
{
    /// <summary>
    /// Creates a message with the given body.
    /// </summary>
    /// <param name="body">The plain-text body.</param>
    /// <returns>The message.</returns>
    private static MailMessage CreateMessage(string body)
    {
        return MailMessage.Create(
            Guid.NewGuid(), Guid.NewGuid(), 1, "s", "Alice", "alice@example.com", "me@example.com", DateTimeOffset.UtcNow, body);
    }

    /// <summary>
    /// Verifies whitespace is collapsed in the preview.
    /// </summary>
    [Fact]
    public void Create_BodyWithWhitespace_CollapsesPreview()
    {
        var message = CreateMessage("  Hello\r\n\r\n   world \t again  ");

        message.Preview.Should().Be("Hello world again");
    }

    /// <summary>
    /// Verifies long bodies are truncated in the preview.
    /// </summary>
    [Fact]
    public void Create_LongBody_TruncatesPreviewTo200Characters()
    {
        var message = CreateMessage(new string('a', 1000));

        message.Preview.Should().HaveLength(200);
    }

    /// <summary>
    /// Verifies a sender address is required.
    /// </summary>
    [Fact]
    public void Create_BlankFromAddress_Throws()
    {
        var act = () => MailMessage.Create(
            Guid.NewGuid(), Guid.NewGuid(), 1, "s", "A", " ", "r", DateTimeOffset.UtcNow, "b");

        act.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// Verifies the read and flagged states can be changed.
    /// </summary>
    [Fact]
    public void SetReadAndSetFlagged_Called_UpdatesState()
    {
        var message = CreateMessage("b");

        message.SetRead(true);
        message.SetFlagged(true);

        message.IsRead.Should().BeTrue();
        message.IsFlagged.Should().BeTrue();
    }

    /// <summary>
    /// Verifies changing the read state marks the flags as pending sync.
    /// </summary>
    [Fact]
    public void SetRead_ChangedValue_MarksPending()
    {
        var message = CreateMessage("b");

        message.SetRead(true);

        message.IsFlagSyncPending.Should().BeTrue();
    }

    /// <summary>
    /// Verifies setting the same read state does not mark the flags as pending.
    /// </summary>
    [Fact]
    public void SetRead_SameValue_DoesNotMarkPending()
    {
        var message = CreateMessage("b");

        message.SetRead(false);

        message.IsFlagSyncPending.Should().BeFalse();
    }

    /// <summary>
    /// Verifies changing the flagged state marks the flags as pending sync.
    /// </summary>
    [Fact]
    public void SetFlagged_ChangedValue_MarksPending()
    {
        var message = CreateMessage("b");

        message.SetFlagged(true);

        message.IsFlagSyncPending.Should().BeTrue();
    }

    /// <summary>
    /// Verifies marking flags as synced clears the pending marker.
    /// </summary>
    [Fact]
    public void MarkFlagsSynced_ClearsPending()
    {
        var message = CreateMessage("b");
        message.SetRead(true);

        message.MarkFlagsSynced();

        message.IsFlagSyncPending.Should().BeFalse();
    }

    /// <summary>
    /// Verifies server flags are copied when nothing is pending.
    /// </summary>
    [Fact]
    public void ApplyServerFlags_NotPending_CopiesValuesAndReturnsTrue()
    {
        var message = CreateMessage("b");

        var changed = message.ApplyServerFlags(true, true);

        changed.Should().BeTrue();
        message.IsRead.Should().BeTrue();
        message.IsFlagged.Should().BeTrue();
    }

    /// <summary>
    /// Verifies applying identical server flags reports no change.
    /// </summary>
    [Fact]
    public void ApplyServerFlags_NoChange_ReturnsFalse()
    {
        var message = CreateMessage("b");

        var changed = message.ApplyServerFlags(false, false);

        changed.Should().BeFalse();
    }

    /// <summary>
    /// Verifies local pending changes win over server values.
    /// </summary>
    [Fact]
    public void ApplyServerFlags_Pending_IgnoresServerValuesAndReturnsFalse()
    {
        var message = CreateMessage("b");
        message.SetRead(true);

        var changed = message.ApplyServerFlags(false, true);

        changed.Should().BeFalse();
        message.IsRead.Should().BeTrue();
        message.IsFlagged.Should().BeFalse();
    }

    /// <summary>
    /// Verifies applying server flags does not mark the message as pending.
    /// </summary>
    [Fact]
    public void ApplyServerFlags_DoesNotMarkPending()
    {
        var message = CreateMessage("b");

        message.ApplyServerFlags(true, true);

        message.IsFlagSyncPending.Should().BeFalse();
    }

    /// <summary>
    /// Verifies a newly created message is not pending, even when created as read.
    /// </summary>
    [Fact]
    public void Create_NewMessage_IsNotPending()
    {
        var message = MailMessage.Create(
            Guid.NewGuid(), Guid.NewGuid(), 1, "s", "A", "a@example.com", "r", DateTimeOffset.UtcNow, "b", isRead: true);

        message.IsFlagSyncPending.Should().BeFalse();
    }

    /// <summary>
    /// Verifies attachments are linked to the message.
    /// </summary>
    [Fact]
    public void AddAttachment_ValidInput_LinksToMessage()
    {
        var message = CreateMessage("b");

        message.AddAttachment("a.txt", "text/plain", 10);

        message.Attachments.Should().ContainSingle().Which.MessageId.Should().Be(message.Id);
    }

    /// <summary>
    /// Verifies the first UIDVALIDITY is not treated as an invalidation.
    /// </summary>
    [Fact]
    public void UpdateUidValidity_FirstValue_ReturnsFalse()
    {
        var folder = MailFolder.Create(Guid.NewGuid(), "INBOX", "Inbox", FolderRole.Inbox);

        folder.UpdateUidValidity(1).Should().BeFalse();
    }

    /// <summary>
    /// Verifies a changed UIDVALIDITY signals invalidation while an unchanged one does not.
    /// </summary>
    [Fact]
    public void UpdateUidValidity_ChangedValue_ReturnsTrue()
    {
        var folder = MailFolder.Create(Guid.NewGuid(), "INBOX", "Inbox", FolderRole.Inbox);
        folder.UpdateUidValidity(1);

        folder.UpdateUidValidity(1).Should().BeFalse();
        folder.UpdateUidValidity(2).Should().BeTrue();
    }

    /// <summary>
    /// Verifies a copy keeps the content and attachment order but gets its own identity, folder, UID and read state.
    /// </summary>
    [Fact]
    public void CopyToFolder_MessageWithAttachments_CopiesContentAndAttachmentsInOrder()
    {
        var accountId = Guid.NewGuid();
        var original = MailMessage.Create(
            accountId, Guid.NewGuid(), 5, "Subject", "Alice", "alice@example.com", "me@example.com",
            new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), "text body", "<p>html</p>", "id-1@example.com", isRead: false);
        original.AddAttachment("a.pdf", "application/pdf", 10);
        original.AddAttachment("b.png", "image/png", 20);
        var folderId = Guid.NewGuid();

        var copy = original.CopyToFolder(folderId, 99, isRead: true);

        copy.Id.Should().NotBe(original.Id);
        copy.AccountId.Should().Be(accountId);
        copy.FolderId.Should().Be(folderId);
        copy.Uid.Should().Be(99u);
        copy.IsRead.Should().BeTrue();
        copy.IsFlagged.Should().BeFalse();
        copy.IsFlagSyncPending.Should().BeFalse();
        copy.Subject.Should().Be("Subject");
        copy.BodyText.Should().Be("text body");
        copy.BodyHtml.Should().Be("<p>html</p>");
        copy.MessageIdHeader.Should().Be("id-1@example.com");
        copy.SentAt.Should().Be(original.SentAt);
        copy.Preview.Should().Be(original.Preview);
        copy.Attachments.Select(a => (a.Ordinal, a.FileName, a.Size)).Should().Equal((0, "a.pdf", 10L), (1, "b.png", 20L));
        copy.Attachments.Select(a => a.MessageId).Should().OnlyContain(id => id == copy.Id);
        original.Attachments.Should().HaveCount(2);
    }
}
