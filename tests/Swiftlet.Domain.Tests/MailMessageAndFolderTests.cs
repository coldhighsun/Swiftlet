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
}
