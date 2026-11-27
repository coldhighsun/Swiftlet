using AwesomeAssertions;
using Swiftlet.Domain.Accounts;

namespace Swiftlet.Domain.Tests;

/// <summary>
/// Tests for <see cref="MailAccount"/>.
/// </summary>
public class MailAccountTests
{
    /// <summary>
    /// A sample IMAP endpoint.
    /// </summary>
    private static readonly ServerEndpoint Imap = new("imap.example.com", 993, true);

    /// <summary>
    /// A sample SMTP endpoint.
    /// </summary>
    private static readonly ServerEndpoint Smtp = new("smtp.example.com", 465, true);

    /// <summary>
    /// Verifies a valid account is created with trimmed values and default interval.
    /// </summary>
    [Fact]
    public void Create_ValidInput_ReturnsAccount()
    {
        var now = DateTimeOffset.UtcNow;

        var account = MailAccount.Create(" Me ", "me@example.com", AuthMethod.Password, Imap, Smtp, now);

        account.DisplayName.Should().Be("Me");
        account.CreatedAt.Should().Be(now);
        account.SyncInterval.Should().Be(TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// Verifies invalid email addresses are rejected.
    /// </summary>
    /// <param name="email">The email under test.</param>
    [Theory]
    [InlineData("nope")]
    [InlineData("@example.com")]
    [InlineData("me@")]
    public void Create_InvalidEmail_Throws(string email)
    {
        var act = () => MailAccount.Create("Me", email, AuthMethod.Password, Imap, Smtp, DateTimeOffset.UtcNow);

        act.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// Verifies OAuth accounts need a provider and password accounts never keep one.
    /// </summary>
    [Fact]
    public void Create_OAuthProviderRules_AreEnforced()
    {
        var withoutProvider = () => MailAccount.Create("Me", "me@example.com", AuthMethod.OAuth2, Imap, Smtp, DateTimeOffset.UtcNow);
        withoutProvider.Should().Throw<ArgumentException>();

        var oauth = MailAccount.Create("Me", "me@example.com", AuthMethod.OAuth2, Imap, Smtp, DateTimeOffset.UtcNow, OAuthProvider.Google);
        oauth.OAuthProvider.Should().Be(OAuthProvider.Google);

        var password = MailAccount.Create("Me", "me@example.com", AuthMethod.Password, Imap, Smtp, DateTimeOffset.UtcNow, OAuthProvider.Google);
        password.OAuthProvider.Should().BeNull();
    }

    [Fact]
    public void ChangeDisplayName_TypedName_IsTrimmedAndMarkedCustom()
    {
        var account = MailAccount.Create("me", "me@example.com", AuthMethod.Password, Imap, Smtp, DateTimeOffset.UtcNow);

        account.ChangeDisplayName("  Work  ");

        account.DisplayName.Should().Be("Work");
        account.HasCustomDisplayName.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void ChangeDisplayName_BlankAfterCustomName_FallsBackToLocalPart(string? name)
    {
        var account = MailAccount.Create(
            "Work", "me@example.com", AuthMethod.Password, Imap, Smtp, DateTimeOffset.UtcNow, hasCustomDisplayName: true);

        account.ChangeDisplayName(name);

        account.DisplayName.Should().Be("me");
        account.HasCustomDisplayName.Should().BeFalse();
    }

    [Fact]
    public void ChangeDisplayName_BlankWithoutCustomName_KeepsFilledInName()
    {
        var account = MailAccount.Create(
            "Provider Name", "me@example.com", AuthMethod.OAuth2, Imap, Smtp, DateTimeOffset.UtcNow, OAuthProvider.Google);

        account.ChangeDisplayName(string.Empty);

        account.DisplayName.Should().Be("Provider Name");
        account.HasCustomDisplayName.Should().BeFalse();
    }

    [Fact]
    public void ChangeServers_PasswordAccount_ReplacesBothEndpoints()
    {
        var account = MailAccount.Create("Me", "me@example.com", AuthMethod.Password, Imap, Smtp, DateTimeOffset.UtcNow);
        var imap = new ServerEndpoint("imap2.example.com", 143, false);
        var smtp = new ServerEndpoint("smtp2.example.com", 587, false);

        account.ChangeServers(imap, smtp);

        account.Imap.Should().Be(imap);
        account.Smtp.Should().Be(smtp);
    }

    [Theory]
    [InlineData(" ", 993)]
    [InlineData("imap.example.com", 0)]
    [InlineData("imap.example.com", 65536)]
    public void ChangeServers_InvalidEndpoint_ThrowsAndKeepsBoth(string host, int port)
    {
        var account = MailAccount.Create("Me", "me@example.com", AuthMethod.Password, Imap, Smtp, DateTimeOffset.UtcNow);

        var act = () => account.ChangeServers(Imap with { Host = "other.example.com" }, new ServerEndpoint(host, port, true));

        act.Should().Throw<ArgumentException>();
        account.Imap.Should().Be(Imap);
        account.Smtp.Should().Be(Smtp);
    }

    [Fact]
    public void ChangeServers_OAuthAccount_Throws()
    {
        var account = MailAccount.Create(
            "Me", "me@example.com", AuthMethod.OAuth2, Imap, Smtp, DateTimeOffset.UtcNow, OAuthProvider.Google);

        var act = () => account.ChangeServers(Imap, Smtp);

        act.Should().Throw<InvalidOperationException>();
    }

    /// <summary>
    /// Verifies a too-short interval is rejected.
    /// </summary>
    [Fact]
    public void ChangeSyncInterval_BelowOneMinute_Throws()
    {
        var account = MailAccount.Create("Me", "me@example.com", AuthMethod.Password, Imap, Smtp, DateTimeOffset.UtcNow);

        var act = () => account.ChangeSyncInterval(TimeSpan.FromSeconds(10));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
