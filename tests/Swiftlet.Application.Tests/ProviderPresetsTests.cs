using AwesomeAssertions;
using Swiftlet.Application.Accounts;
using Swiftlet.Domain.Accounts;

namespace Swiftlet.Application.Tests;

/// <summary>
/// Tests for <see cref="ProviderPresets"/>.
/// </summary>
public class ProviderPresetsTests
{
    /// <summary>
    /// Verifies known providers resolve, ignoring case and surrounding whitespace.
    /// </summary>
    /// <param name="email">The email address.</param>
    /// <param name="imapHost">The expected IMAP host.</param>
    [Theory]
    [InlineData("me@gmail.com", "imap.gmail.com")]
    [InlineData("Me@GMAIL.COM", "imap.gmail.com")]
    [InlineData("me@outlook.com", "outlook.office365.com")]
    [InlineData("me@hotmail.com", "outlook.office365.com")]
    [InlineData("me@qq.com", "imap.qq.com")]
    [InlineData("me@163.com", "imap.163.com")]
    [InlineData("me@icloud.com", "imap.mail.me.com")]
    public void TryGet_KnownDomain_ReturnsPreset(string email, string imapHost)
    {
        var found = ProviderPresets.TryGet(email, out var preset);

        found.Should().BeTrue();
        preset.Imap.Host.Should().Be(imapHost);
        preset.Imap.UseSsl.Should().BeTrue();
    }

    /// <summary>
    /// Verifies unknown or malformed addresses do not resolve.
    /// </summary>
    /// <param name="email">The email address.</param>
    [Theory]
    [InlineData("me@unknown-company.example")]
    [InlineData("no-at-sign")]
    [InlineData("trailing@")]
    [InlineData("")]
    [InlineData("  ")]
    public void TryGet_UnknownOrInvalid_ReturnsFalse(string email)
    {
        ProviderPresets.TryGet(email, out _).Should().BeFalse();
    }

    /// <summary>
    /// Verifies providers that store Sent copies themselves are flagged, so the client does not duplicate them.
    /// </summary>
    [Fact]
    public void TryGet_GmailAndQq_DifferInSentCopyBehavior()
    {
        ProviderPresets.TryGet("a@gmail.com", out var gmail);
        ProviderPresets.TryGet("a@qq.com", out var qq);

        gmail.SavesSentCopyAutomatically.Should().BeTrue();
        qq.SavesSentCopyAutomatically.Should().BeFalse();
    }

    /// <summary>
    /// Verifies only Microsoft organization accounts get the Microsoft 365 SMTP server.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <param name="isOrganizationAccount">Whether the account belongs to an organization.</param>
    /// <param name="smtpHost">The expected SMTP host.</param>
    [Theory]
    [InlineData(OAuthProvider.Microsoft, false, "smtp-mail.outlook.com")]
    [InlineData(OAuthProvider.Microsoft, true, "smtp.office365.com")]
    [InlineData(OAuthProvider.Google, false, "smtp.gmail.com")]
    [InlineData(OAuthProvider.Google, true, "smtp.gmail.com")]
    public void For_ProviderAndAccountType_SelectsSmtpServer(OAuthProvider provider, bool isOrganizationAccount, string smtpHost)
    {
        ProviderPresets.For(provider, isOrganizationAccount).Smtp.Host.Should().Be(smtpHost);
    }

    /// <summary>
    /// Verifies an organization's own domain is recognized by its SMTP server when deciding about Sent copies.
    /// </summary>
    /// <param name="email">The account address.</param>
    /// <param name="smtpHost">The account's SMTP host.</param>
    /// <param name="expected">Whether the provider stores the copy itself.</param>
    [Theory]
    [InlineData("me@contoso.example", "smtp.office365.com", true)]
    [InlineData("me@contoso.example", "SMTP.Office365.com", true)]
    [InlineData("me@contoso.example", "smtp.gmail.com", true)]
    [InlineData("me@gmail.com", "smtp.relay.example", true)]
    [InlineData("me@contoso.example", "smtp.qq.com", false)]
    [InlineData("me@contoso.example", "smtp.contoso.example", false)]
    public void SavesSentCopyAutomatically_Account_ChecksDomainAndSmtpHost(string email, string smtpHost, bool expected)
    {
        var account = MailAccount.Create(
            "Me",
            email,
            AuthMethod.Password,
            new ServerEndpoint("imap.contoso.example", 993, true),
            new ServerEndpoint(smtpHost, 587, false),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        ProviderPresets.SavesSentCopyAutomatically(account).Should().Be(expected);
    }
}
