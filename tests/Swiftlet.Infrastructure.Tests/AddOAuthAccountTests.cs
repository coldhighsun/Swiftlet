using AwesomeAssertions;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Accounts;
using Swiftlet.Domain.Accounts;
using Swiftlet.Infrastructure.Persistence.Repositories;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="AddOAuthAccountCommandHandler"/> and OAuth account persistence.
/// </summary>
public class AddOAuthAccountTests
{
    /// <summary>
    /// A successful sign-in result.
    /// </summary>
    private static readonly OAuthAuthorization SignedIn = new(
        "me@gmail.com",
        "Me Myself",
        new OAuthTokens("access", "refresh", new DateTimeOffset(2026, 1, 1, 1, 0, 0, TimeSpan.Zero)));

    /// <summary>
    /// Verifies the account is created with the provider's servers, the tokens are stored and the login verified.
    /// </summary>
    [Fact]
    public async Task Handle_SignInSucceeds_SavesAccountAndTokens()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var secrets = new InMemorySecretStore();
        var factory = new FakeImapSessionFactory();
        var authorizer = new FakeOAuthAuthorizer { Result = SignedIn };
        var handler = new AddOAuthAccountCommandHandler(db.CreateAccountRepository(), secrets, authorizer, factory, TimeProvider.System);

        var id = await handler.Handle(new AddOAuthAccountCommand(OAuthProvider.Google), ct);

        await using var verify = db.CreateVerifier();
        var saved = await verify.Accounts().GetAsync(id, ct);
        saved!.AuthMethod.Should().Be(AuthMethod.OAuth2);
        saved.OAuthProvider.Should().Be(OAuthProvider.Google);
        saved.DisplayName.Should().Be("Me Myself");
        saved.HasCustomDisplayName.Should().BeFalse();
        saved.Imap.Host.Should().Be("imap.gmail.com");
        (await OAuthTokenStorage.LoadAsync(secrets, id, ct)).Should().Be(SignedIn.Tokens);
        authorizer.Requested.Should().Equal(OAuthProvider.Google);
        factory.ConnectCount.Should().Be(1);
    }

    /// <summary>
    /// Verifies the Microsoft provider uses the Outlook servers and an explicit display name wins.
    /// </summary>
    [Fact]
    public async Task Handle_MicrosoftWithDisplayName_UsesOutlookServers()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var authorizer = new FakeOAuthAuthorizer { Result = SignedIn with { EmailAddress = "me@contoso.example" } };
        var handler = new AddOAuthAccountCommandHandler(
            db.CreateAccountRepository(), new InMemorySecretStore(), authorizer, new FakeImapSessionFactory(), TimeProvider.System);

        var id = await handler.Handle(new AddOAuthAccountCommand(OAuthProvider.Microsoft, "Work"), ct);

        var saved = await db.CreateAccountRepository().GetAsync(id, ct);
        saved!.OAuthProvider.Should().Be(OAuthProvider.Microsoft);
        saved.DisplayName.Should().Be("Work");
        saved.HasCustomDisplayName.Should().BeTrue();
        saved.Imap.Host.Should().Be("outlook.office365.com");
        saved.Smtp.Port.Should().Be(587);
    }

    /// <summary>
    /// Verifies a failed IMAP login leaves no account and no stored tokens.
    /// </summary>
    [Fact]
    public async Task Handle_ImapLoginFails_RemovesTokensAndSavesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var secrets = new InMemorySecretStore();
        var factory = new FakeImapSessionFactory { Failure = new InvalidOperationException("IMAP access is disabled") };
        var handler = new AddOAuthAccountCommandHandler(
            db.CreateAccountRepository(), secrets, new FakeOAuthAuthorizer { Result = SignedIn }, factory, TimeProvider.System);

        var act = async () => await handler.Handle(new AddOAuthAccountCommand(OAuthProvider.Google), ct);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("IMAP access is disabled");
        secrets.Values.Should().BeEmpty();
        (await db.CreateAccountRepository().GetAllAsync(ct)).Should().BeEmpty();
    }

    /// <summary>
    /// Verifies signing in with an address that already exists stores nothing.
    /// </summary>
    [Fact]
    public async Task Handle_DuplicateAccount_ThrowsWithoutStoringTokens()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var repository = db.CreateAccountRepository();
        await repository.AddAsync(OAuthTestData.OAuthAccount(email: "ME@gmail.com"), ct);
        var secrets = new InMemorySecretStore();
        var handler = new AddOAuthAccountCommandHandler(
            repository, secrets, new FakeOAuthAuthorizer { Result = SignedIn }, new FakeImapSessionFactory(), TimeProvider.System);

        var act = async () => await handler.Handle(new AddOAuthAccountCommand(OAuthProvider.Google), ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
        secrets.Values.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies sign-in failures such as a missing configuration propagate unchanged.
    /// </summary>
    [Fact]
    public async Task Handle_AuthorizerFails_PropagatesException()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var authorizer = new FakeOAuthAuthorizer { Failure = new OAuthNotConfiguredException(OAuthProvider.Google, "hint") };
        var handler = new AddOAuthAccountCommandHandler(
            db.CreateAccountRepository(), new InMemorySecretStore(), authorizer, new FakeImapSessionFactory(), TimeProvider.System);

        var act = async () => await handler.Handle(new AddOAuthAccountCommand(OAuthProvider.Google), ct);

        await act.Should().ThrowAsync<OAuthNotConfiguredException>();
    }

    /// <summary>
    /// Verifies the provider survives a database round trip.
    /// </summary>
    [Fact]
    public async Task AddAsync_OAuthAccount_PersistsProvider()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var account = OAuthTestData.OAuthAccount(OAuthProvider.Microsoft, "me@outlook.com");
        var passwordAccount = TestData.Account("pw@example.com");
        await db.CreateAccountRepository().AddAsync(account, ct);
        await db.CreateAccountRepository().AddAsync(passwordAccount, ct);

        await using var verify = db.CreateVerifier();
        var repository = verify.Accounts();

        (await repository.GetAsync(account.Id, ct))!.OAuthProvider.Should().Be(OAuthProvider.Microsoft);
        (await repository.GetAsync(passwordAccount.Id, ct))!.OAuthProvider.Should().BeNull();
    }
}
