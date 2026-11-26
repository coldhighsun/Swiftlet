using AwesomeAssertions;
using Swiftlet.Application.Abstractions;
using Swiftlet.Domain.Folders;
using Swiftlet.Infrastructure.Mail;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for finding the Sent folder in a folder listing.
/// </summary>
public class SentFolderLocatorTests
{
    /// <summary>
    /// Verifies the folder with the Sent role is chosen, whatever it is called.
    /// </summary>
    [Fact]
    public void Find_FolderWithSentRole_ReturnsItsFullName()
    {
        RemoteFolder[] folders =
        [
            new("INBOX", "INBOX", FolderRole.Inbox, true),
            new("Sent Items", "Sent Items", FolderRole.Other, true),
            new("Sent Messages", "Sent Messages", FolderRole.Sent, true),
        ];

        var found = SentFolderLocator.Find(folders);

        found.Should().Be("Sent Messages");
    }

    /// <summary>
    /// Verifies a folder is not picked by its name alone.
    /// </summary>
    [Fact]
    public void Find_NoFolderWithSentRole_ReturnsNull()
    {
        RemoteFolder[] folders = [new("Sent", "Sent", FolderRole.Other, true)];

        var found = SentFolderLocator.Find(folders);

        found.Should().BeNull();
    }

    /// <summary>
    /// Verifies a Sent folder that cannot hold messages is skipped.
    /// </summary>
    [Fact]
    public void Find_SentFolderNotSelectable_ReturnsNull()
    {
        RemoteFolder[] folders = [new("Sent", "Sent", FolderRole.Sent, false)];

        var found = SentFolderLocator.Find(folders);

        found.Should().BeNull();
    }
}
