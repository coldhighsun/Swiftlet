using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Accounts;
using Swiftlet.Domain.Accounts;
using Swiftlet.Infrastructure.Persistence.Repositories;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="ReauthenticateOAuthAccountCommandHandler"/>.
/// </summary>
public class ReauthenticateOAuthAccountTests
{
    /// <summary>
    /// The tokens stored before re-signing in.
    /// </summary>
    private static readonly OAuthTokens OldTokens = new("old-access", "old-refresh", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    /// <summary>
    /// The tokens returned by a fresh sign-in.
    /// </summary>
    private static readonly OAuthTokens NewTokens = new("new-access", "new-refresh", new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

    /// <summary>
    /// Verifies a sign-in as the same address (any case) replaces the tokens and leaves the account alone.
    /// </summary>
    [Fact]
    public async Task Handle_MatchingEmail_ReplacesTokensAndKeepsAccount()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var account = OAuthTestData.OAuthAccount(OAuthProvider.Microsoft, "me@outlook.com");
        await db.CreateAccountRepository().AddAsync(account, ct);
        var secrets = new InMemorySecretStore();
        await OAuthTokenStorage.SaveAsync(secrets, account.Id, OldTokens, ct);
        var authorizer = new FakeOAuthAuthorizer { Result = new OAuthAuthorization("ME@Outlook.com", "Me", NewTokens) };
        var factory = new FakeImapSessionFactory();
        var handler = new ReauthenticateOAuthAccountCommandHandler(db.CreateAccountRepository(), secrets, authorizer, factory, NullLogger<ReauthenticateOAuthAccountCommandHandler>.Instance);

        await handler.Handle(new ReauthenticateOAuthAccountCommand(account.Id), ct);

        (await OAuthTokenStorage.LoadAsync(secrets, account.Id, ct)).Should().Be(NewTokens);
        authorizer.Requested.Should().Equal(OAuthProvider.Microsoft);
        factory.ConnectCount.Should().Be(1);
        await using var verify = db.CreateVerifier();
        var all = await verify.Accounts().GetAllAsync(ct);
        all.Should().ContainSingle();
        all[0].EmailAddress.Should().Be("me@outlook.com");
        all[0].DisplayName.Should().Be(account.DisplayName);
    }

    /// <summary>
    /// Verifies signing in as another address is rejected before anything is stored or connected.
    /// </summary>
    [Fact]
    public async Task Handle_DifferentEmail_ThrowsNamingBothAddressesAndStoresNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var account = OAuthTestData.OAuthAccount(email: "me@gmail.com");
        await db.CreateAccountRepository().AddAsync(account, ct);
        var secrets = new InMemorySecretStore();
        await OAuthTokenStorage.SaveAsync(secrets, account.Id, OldTokens, ct);
        var authorizer = new FakeOAuthAuthorizer { Result = new OAuthAuthorization("other@gmail.com", "Other", NewTokens) };
        var factory = new FakeImapSessionFactory();
        var handler = new ReauthenticateOAuthAccountCommandHandler(db.CreateAccountRepository(), secrets, authorizer, factory, NullLogger<ReauthenticateOAuthAccountCommandHandler>.Instance);

        var act = async () => await handler.Handle(new ReauthenticateOAuthAccountCommand(account.Id), ct);

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Contain("other@gmail.com").And.Contain("me@gmail.com");
        (await OAuthTokenStorage.LoadAsync(secrets, account.Id, ct)).Should().Be(OldTokens);
        factory.ConnectCount.Should().Be(0);
    }

    /// <summary>
    /// Verifies a failed verification puts the previous tokens back.
    /// </summary>
    [Fact]
    public async Task Handle_VerificationFails_RestoresPreviousTokens()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var account = OAuthTestData.OAuthAccount();
        await db.CreateAccountRepository().AddAsync(account, ct);
        var secrets = new InMemorySecretStore();
        await OAuthTokenStorage.SaveAsync(secrets, account.Id, OldTokens, ct);
        var authorizer = new FakeOAuthAuthorizer { Result = new OAuthAuthorization("me@gmail.com", "Me", NewTokens) };
        var factory = new FakeImapSessionFactory { Failure = new InvalidOperationException("IMAP access is disabled") };
        var handler = new ReauthenticateOAuthAccountCommandHandler(db.CreateAccountRepository(), secrets, authorizer, factory, NullLogger<ReauthenticateOAuthAccountCommandHandler>.Instance);

        var act = async () => await handler.Handle(new ReauthenticateOAuthAccountCommand(account.Id), ct);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("IMAP access is disabled");
        (await OAuthTokenStorage.LoadAsync(secrets, account.Id, ct)).Should().Be(OldTokens);
    }

    /// <summary>
    /// Verifies a failed verification removes the new tokens when there were none before.
    /// </summary>
    [Fact]
    public async Task Handle_VerificationFailsAndNoPreviousTokens_RemovesNewTokens()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var account = OAuthTestData.OAuthAccount();
        await db.CreateAccountRepository().AddAsync(account, ct);
        var secrets = new InMemorySecretStore();
        var authorizer = new FakeOAuthAuthorizer { Result = new OAuthAuthorization("me@gmail.com", "Me", NewTokens) };
        var factory = new FakeImapSessionFactory { Failure = new InvalidOperationException("IMAP access is disabled") };
        var handler = new ReauthenticateOAuthAccountCommandHandler(db.CreateAccountRepository(), secrets, authorizer, factory, NullLogger<ReauthenticateOAuthAccountCommandHandler>.Instance);

        var act = async () => await handler.Handle(new ReauthenticateOAuthAccountCommand(account.Id), ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
        secrets.Values.Should().NotContainKey(AccountSecrets.OAuthKey(account.Id));
    }

    /// <summary>
    /// Verifies password accounts are refused without opening the browser.
    /// </summary>
    [Fact]
    public async Task Handle_PasswordAccount_ThrowsWithoutAuthorizing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var account = TestData.Account("pw@example.com");
        await db.CreateAccountRepository().AddAsync(account, ct);
        var authorizer = new FakeOAuthAuthorizer { Result = new OAuthAuthorization("pw@example.com", "Pw", NewTokens) };
        var handler = new ReauthenticateOAuthAccountCommandHandler(
            db.CreateAccountRepository(), new InMemorySecretStore(), authorizer, new FakeImapSessionFactory(), NullLogger<ReauthenticateOAuthAccountCommandHandler>.Instance);

        var act = async () => await handler.Handle(new ReauthenticateOAuthAccountCommand(account.Id), ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
        authorizer.Requested.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies an unknown account is an error.
    /// </summary>
    [Fact]
    public async Task Handle_UnknownAccount_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var authorizer = new FakeOAuthAuthorizer();
        var handler = new ReauthenticateOAuthAccountCommandHandler(
            db.CreateAccountRepository(), new InMemorySecretStore(), authorizer, new FakeImapSessionFactory(), NullLogger<ReauthenticateOAuthAccountCommandHandler>.Instance);

        var act = async () => await handler.Handle(new ReauthenticateOAuthAccountCommand(Guid.NewGuid()), ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
        authorizer.Requested.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies a failing restore does not hide the original verification error.
    /// </summary>
    [Fact]
    public async Task Handle_VerificationFailsAndRestoreFails_SurfacesOriginalException()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var account = OAuthTestData.OAuthAccount();
        await db.CreateAccountRepository().AddAsync(account, ct);
        var secrets = new InMemorySecretStore();
        await OAuthTokenStorage.SaveAsync(secrets, account.Id, OldTokens, ct);
        var failing = new SetFailingSecretStore(secrets);
        var authorizer = new FakeOAuthAuthorizer { Result = new OAuthAuthorization("me@gmail.com", "Me", NewTokens) };
        var factory = new FakeImapSessionFactory { Failure = new InvalidOperationException("IMAP access is disabled") };
        var handler = new ReauthenticateOAuthAccountCommandHandler(db.CreateAccountRepository(), failing, authorizer, factory, NullLogger<ReauthenticateOAuthAccountCommandHandler>.Instance);
        failing.AllowedSets = 1;

        var act = async () => await handler.Handle(new ReauthenticateOAuthAccountCommand(account.Id), ct);

        var exception = await act.Should().ThrowAsync<InvalidOperationException>();
        exception.Which.Message.Should().Be("IMAP access is disabled");
        exception.Which.Data["RestoreFailure"].Should().Be("store unavailable");
    }

    /// <summary>
    /// Verifies a corrupt stored value does not block re-signing in.
    /// </summary>
    [Fact]
    public async Task Handle_CorruptStoredTokens_StillStoresNewTokens()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var account = OAuthTestData.OAuthAccount();
        await db.CreateAccountRepository().AddAsync(account, ct);
        var secrets = new InMemorySecretStore();
        secrets.Values[AccountSecrets.OAuthKey(account.Id)] = "{not json";
        var authorizer = new FakeOAuthAuthorizer { Result = new OAuthAuthorization("me@gmail.com", "Me", NewTokens) };
        var handler = new ReauthenticateOAuthAccountCommandHandler(
            db.CreateAccountRepository(), secrets, authorizer, new FakeImapSessionFactory(), NullLogger<ReauthenticateOAuthAccountCommandHandler>.Instance);

        await handler.Handle(new ReauthenticateOAuthAccountCommand(account.Id), ct);

        (await OAuthTokenStorage.LoadAsync(secrets, account.Id, ct)).Should().Be(NewTokens);
    }

    /// <summary>
    /// Verifies a failed verification after a corrupt stored value removes the new tokens.
    /// </summary>
    [Fact]
    public async Task Handle_CorruptStoredTokensAndVerificationFails_RemovesNewTokens()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var account = OAuthTestData.OAuthAccount();
        await db.CreateAccountRepository().AddAsync(account, ct);
        var secrets = new InMemorySecretStore();
        secrets.Values[AccountSecrets.OAuthKey(account.Id)] = "{not json";
        var authorizer = new FakeOAuthAuthorizer { Result = new OAuthAuthorization("me@gmail.com", "Me", NewTokens) };
        var factory = new FakeImapSessionFactory { Failure = new InvalidOperationException("IMAP access is disabled") };
        var handler = new ReauthenticateOAuthAccountCommandHandler(db.CreateAccountRepository(), secrets, authorizer, factory, NullLogger<ReauthenticateOAuthAccountCommandHandler>.Instance);

        var act = async () => await handler.Handle(new ReauthenticateOAuthAccountCommand(account.Id), ct);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("IMAP access is disabled");
        secrets.Values.Should().NotContainKey(AccountSecrets.OAuthKey(account.Id));
    }

    /// <summary>
    /// Verifies an account deleted while the browser is open gets no tokens stored.
    /// </summary>
    [Fact]
    public async Task Handle_AccountDeletedWhileAuthorizing_ThrowsAndStoresNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var account = OAuthTestData.OAuthAccount();
        await db.CreateAccountRepository().AddAsync(account, ct);
        var secrets = new InMemorySecretStore();
        var authorizer = new FakeOAuthAuthorizer { Result = new OAuthAuthorization("me@gmail.com", "Me", NewTokens) };
        authorizer.OnAuthorize = () =>
        {
            // The hook cannot be async, so block on the delete through a second context.
            var other = db.CreateVerifier();
            other.Accounts().DeleteAsync(account.Id).GetAwaiter().GetResult();
            other.DisposeAsync().AsTask().GetAwaiter().GetResult();
        };
        var factory = new FakeImapSessionFactory();
        var handler = new ReauthenticateOAuthAccountCommandHandler(
            db.CreateAccountRepository(), secrets, authorizer, factory, NullLogger<ReauthenticateOAuthAccountCommandHandler>.Instance);

        var act = async () => await handler.Handle(new ReauthenticateOAuthAccountCommand(account.Id), ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
        secrets.Values.Should().NotContainKey(AccountSecrets.OAuthKey(account.Id));
        factory.ConnectCount.Should().Be(0);
    }

    /// <summary>
    /// Verifies tokens rotated by a background refresh during the browser wait are what a failed verification restores.
    /// </summary>
    [Fact]
    public async Task Handle_TokensRotatedWhileAuthorizingAndVerificationFails_RestoresLatestTokens()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var account = OAuthTestData.OAuthAccount();
        await db.CreateAccountRepository().AddAsync(account, ct);
        var secrets = new InMemorySecretStore();
        await OAuthTokenStorage.SaveAsync(secrets, account.Id, OldTokens, ct);
        var rotated = new OAuthTokens("rotated-access", "rotated-refresh", new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        var authorizer = new FakeOAuthAuthorizer { Result = new OAuthAuthorization("me@gmail.com", "Me", NewTokens) };
        authorizer.OnAuthorize = () => OAuthTokenStorage.SaveAsync(secrets, account.Id, rotated, CancellationToken.None).GetAwaiter().GetResult();
        var factory = new FakeImapSessionFactory { Failure = new InvalidOperationException("IMAP access is disabled") };
        var handler = new ReauthenticateOAuthAccountCommandHandler(
            db.CreateAccountRepository(), secrets, authorizer, factory, NullLogger<ReauthenticateOAuthAccountCommandHandler>.Instance);

        var act = async () => await handler.Handle(new ReauthenticateOAuthAccountCommand(account.Id), ct);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("IMAP access is disabled");
        (await OAuthTokenStorage.LoadAsync(secrets, account.Id, ct)).Should().Be(rotated);
    }

    /// <summary>
    /// A secret store that fails writes once its allowance of successful writes is used up.
    /// </summary>
    /// <param name="inner">The wrapped store.</param>
    private sealed class SetFailingSecretStore(InMemorySecretStore inner) : ISecretStore
    {
        /// <summary>
        /// Gets or sets how many more writes succeed before they start failing.
        /// </summary>
        public int AllowedSets { get; set; } = int.MaxValue;

        /// <inheritdoc />
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            return inner.GetAsync(key, cancellationToken);
        }

        /// <inheritdoc />
        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            if (AllowedSets <= 0)
            {
                return Task.FromException(new IOException("store unavailable"));
            }

            AllowedSets--;
            return inner.SetAsync(key, value, cancellationToken);
        }

        /// <inheritdoc />
        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            return inner.RemoveAsync(key, cancellationToken);
        }
    }

    /// <summary>
    /// Verifies a failed sign-in keeps the old tokens.
    /// </summary>
    [Fact]
    public async Task Handle_AuthorizerFails_KeepsOldTokens()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var account = OAuthTestData.OAuthAccount();
        await db.CreateAccountRepository().AddAsync(account, ct);
        var secrets = new InMemorySecretStore();
        await OAuthTokenStorage.SaveAsync(secrets, account.Id, OldTokens, ct);
        var authorizer = new FakeOAuthAuthorizer { Failure = new OAuthNotConfiguredException(OAuthProvider.Google, "hint") };
        var handler = new ReauthenticateOAuthAccountCommandHandler(
            db.CreateAccountRepository(), secrets, authorizer, new FakeImapSessionFactory(), NullLogger<ReauthenticateOAuthAccountCommandHandler>.Instance);

        var act = async () => await handler.Handle(new ReauthenticateOAuthAccountCommand(account.Id), ct);

        await act.Should().ThrowAsync<OAuthNotConfiguredException>();
        (await OAuthTokenStorage.LoadAsync(secrets, account.Id, ct)).Should().Be(OldTokens);
    }
}
