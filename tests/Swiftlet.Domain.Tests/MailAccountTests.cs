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
