using AwesomeAssertions;
using Swiftlet.Application.Messages;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;

namespace Swiftlet.Application.Tests;

/// <summary>
/// Tests for <see cref="ArchiveFolderResolver"/>.
/// </summary>
public class ArchiveFolderResolverTests
{
    [Fact]
    public void Resolve_NothingChosen_PrefersTheArchiveRole()
    {
        var account = CreateAccount();
        var allMail = CreateFolder(account, "All", FolderRole.AllMail);
        var archive = CreateFolder(account, "Archive", FolderRole.Archive);

        var result = ArchiveFolderResolver.Resolve(account, [allMail, archive]);

        result.Should().BeSameAs(archive);
    }

    [Fact]
    public void Resolve_NothingChosenAndNoArchive_UsesAllMail()
    {
        var account = CreateAccount();
        var allMail = CreateFolder(account, "All", FolderRole.AllMail);

        var result = ArchiveFolderResolver.Resolve(account, [CreateFolder(account, "INBOX", FolderRole.Inbox), allMail]);

        result.Should().BeSameAs(allMail);
    }

    [Fact]
    public void Resolve_NothingChosenAndNoSuitableRole_ReturnsNull()
    {
        var account = CreateAccount();

        var result = ArchiveFolderResolver.Resolve(account, [CreateFolder(account, "Archive", FolderRole.Other)]);

        result.Should().BeNull();
    }

    [Fact]
    public void Resolve_FolderChosen_WinsOverTheRoles()
    {
        var account = CreateAccount();
        account.ChangeArchiveFolder("Done");
        var done = CreateFolder(account, "Done", FolderRole.Other);

        var result = ArchiveFolderResolver.Resolve(account, [CreateFolder(account, "Archive", FolderRole.Archive), done]);

        result.Should().BeSameAs(done);
    }

    [Theory]
    [InlineData("Gone")]
    [InlineData("archive")]
    public void Resolve_ChosenFolderMissing_DoesNotFallBack(string chosen)
    {
        var account = CreateAccount();
        account.ChangeArchiveFolder(chosen);

        var result = ArchiveFolderResolver.Resolve(account, [CreateFolder(account, "Archive", FolderRole.Archive)]);

        result.Should().BeNull();
    }

    [Fact]
    public void Resolve_FolderOfAnotherAccount_IsIgnored()
    {
        var other = CreateAccount();

        var result = ArchiveFolderResolver.Resolve(CreateAccount(), [CreateFolder(other, "Archive", FolderRole.Archive)]);

        result.Should().BeNull();
    }

    /// <summary>
    /// Creates an account.
    /// </summary>
    /// <returns>The account.</returns>
    private static MailAccount CreateAccount()
    {
        return MailAccount.Create(
            "Me",
            "me@example.com",
            AuthMethod.Password,
            new ServerEndpoint("imap.example.com", 993, true),
            new ServerEndpoint("smtp.example.com", 465, true),
            DateTimeOffset.UnixEpoch);
    }

    /// <summary>
    /// Creates a folder of an account.
    /// </summary>
    /// <param name="account">The owning account.</param>
    /// <param name="name">The folder name.</param>
    /// <param name="role">The folder role.</param>
    /// <returns>The folder.</returns>
    private static MailFolder CreateFolder(MailAccount account, string name, FolderRole role)
    {
        return MailFolder.Create(account.Id, name, name, role);
    }
}
