using AwesomeAssertions;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Accounts;
using Swiftlet.Application.Sync;
using Swiftlet.Domain.Accounts;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="UpdateAccountCommandHandler"/> with a real SQLite account store.
/// </summary>
public class UpdateAccountTests
{
    /// <summary>
    /// A replacement IMAP endpoint.
    /// </summary>
    private static readonly ServerEndpoint NewImap = new("imap2.example.com", 143, false);

    /// <summary>
    /// A replacement SMTP endpoint.
    /// </summary>
    private static readonly ServerEndpoint NewSmtp = new("smtp2.example.com", 587, false);

    [Fact]
    public async Task Handle_NameAndIntervalOnly_SavesWithoutConnecting()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        s.Factory.Failure = new IOException("offline");

        // Act
        var result = await s.Handler.Handle(
            new UpdateAccountCommand(s.Account.Id, "Work", TimeSpan.FromMinutes(15), s.Account.Imap with { }, s.Account.Smtp with { }), ct);

        // Assert
        result.ConnectionChanged.Should().BeFalse();
        s.Factory.ConnectCount.Should().Be(0);
        s.Revisions.Get(s.Account.Id).Should().Be(0);
        var stored = await s.StoredAsync(ct);
        stored.DisplayName.Should().Be("Work");
        stored.HasCustomDisplayName.Should().BeTrue();
        stored.SyncInterval.Should().Be(TimeSpan.FromMinutes(15));
        s.StoredPassword.Should().Be("old-password");
    }

    [Fact]
    public async Task Handle_ArchiveFolder_SavesAndClearsItWithoutConnecting()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        s.Factory.Failure = new IOException("offline");
        var command = new UpdateAccountCommand(s.Account.Id, string.Empty, TimeSpan.FromMinutes(5), ArchiveFolderName: "Done");

        // Act
        var result = await s.Handler.Handle(command, ct);
        var chosen = (await s.StoredAsync(ct)).ArchiveFolderName;
        await s.Handler.Handle(command with { ArchiveFolderName = null }, ct);

        // Assert
        result.ConnectionChanged.Should().BeFalse();
        s.Factory.ConnectCount.Should().Be(0);
        chosen.Should().Be("Done");
        (await s.StoredAsync(ct)).ArchiveFolderName.Should().BeNull();
    }

    [Fact]
    public async Task Handle_Color_SavesWithoutConnectingAndKeepsItWhenOmitted()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        s.Factory.Failure = new IOException("offline");
        var command = new UpdateAccountCommand(s.Account.Id, string.Empty, TimeSpan.FromMinutes(5), Color: "#5E63D6");

        // Act
        var result = await s.Handler.Handle(command, ct);
        await s.Handler.Handle(command with { Color = null }, ct);

        // Assert
        result.ConnectionChanged.Should().BeFalse();
        s.Factory.ConnectCount.Should().Be(0);
        (await s.StoredAsync(ct)).Color.Should().Be("#5E63D6");
    }

    [Fact]
    public async Task Handle_NewServers_VerifiesWithStoredPasswordAndSaves()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);

        // Act
        var result = await s.Handler.Handle(
            new UpdateAccountCommand(s.Account.Id, string.Empty, TimeSpan.FromMinutes(5), NewImap, NewSmtp), ct);

        // Assert
        result.ConnectionChanged.Should().BeTrue();
        s.Factory.ConnectCount.Should().Be(1);
        s.Factory.Passwords.Should().BeEmpty("the stored password is used");
        s.Factory.Session.Disposed.Should().BeTrue();
        s.Revisions.Get(s.Account.Id).Should().Be(1);
        var stored = await s.StoredAsync(ct);
        stored.Imap.Should().Be(NewImap);
        stored.Smtp.Should().Be(NewSmtp);
    }

    [Fact]
    public async Task Handle_OnlySmtpChanged_SavesWithoutVerification()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);

        // Act
        var result = await s.Handler.Handle(
            new UpdateAccountCommand(s.Account.Id, string.Empty, TimeSpan.FromMinutes(5), Smtp: NewSmtp), ct);

        // Assert
        result.ConnectionChanged.Should().BeFalse();
        s.Factory.ConnectCount.Should().Be(0);
        var stored = await s.StoredAsync(ct);
        stored.Smtp.Should().Be(NewSmtp);
        stored.Imap.Should().Be(s.Account.Imap);
    }

    [Fact]
    public async Task Handle_NewPassword_VerifiesItThenStoresIt()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);

        // Act
        var result = await s.Handler.Handle(
            new UpdateAccountCommand(s.Account.Id, string.Empty, TimeSpan.FromMinutes(5), Password: "new-password"), ct);

        // Assert
        result.ConnectionChanged.Should().BeTrue();
        s.Factory.Passwords.Should().Equal("new-password");
        s.StoredPassword.Should().Be("new-password");
        s.Revisions.Get(s.Account.Id).Should().Be(1);
    }

    [Fact]
    public async Task Handle_EmptyPassword_KeepsStoredPassword()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);

        // Act
        var result = await s.Handler.Handle(
            new UpdateAccountCommand(s.Account.Id, string.Empty, TimeSpan.FromMinutes(5), Password: string.Empty), ct);

        // Assert
        result.ConnectionChanged.Should().BeFalse();
        s.StoredPassword.Should().Be("old-password");
    }

    [Fact]
    public async Task Handle_VerificationFails_ChangesNothing()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);
        s.Factory.Failure = new InvalidOperationException("authentication failed");

        // Act
        var act = async () => await s.Handler.Handle(
            new UpdateAccountCommand(s.Account.Id, "Work", TimeSpan.FromMinutes(30), NewImap, NewSmtp, "wrong"), ct);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("authentication failed");
        s.StoredPassword.Should().Be("old-password");
        s.Revisions.Get(s.Account.Id).Should().Be(0);
        var stored = await s.StoredAsync(ct);
        stored.DisplayName.Should().Be(s.Account.DisplayName);
        stored.SyncInterval.Should().Be(TimeSpan.FromMinutes(5));
        stored.Imap.Host.Should().Be("imap.example.com");
    }

    [Fact]
    public async Task Handle_SaveFails_RestoresPreviousPassword()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);

        // The account disappears between the read and the save, so writing the row fails.
        s.Factory.CreateSession = _ =>
        {
            var other = s.Database.CreateVerifier();
            other.Accounts().DeleteAsync(s.Account.Id).GetAwaiter().GetResult();
            other.DisposeAsync().AsTask().GetAwaiter().GetResult();
            return s.Factory.Session;
        };

        // Act
        var act = async () => await s.Handler.Handle(
            new UpdateAccountCommand(s.Account.Id, "Work", TimeSpan.FromMinutes(5), Password: "new-password"), ct);

        // Assert
        await act.Should().ThrowAsync<Exception>();
        s.StoredPassword.Should().Be("old-password");
        s.Revisions.Get(s.Account.Id).Should().Be(0);
    }

    [Fact]
    public async Task Handle_OAuthAccount_RenamesWithoutConnecting()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct, OAuthTestData.OAuthAccount());

        // Act
        await s.Handler.Handle(new UpdateAccountCommand(s.Account.Id, "Private", TimeSpan.FromMinutes(2)), ct);

        // Assert
        s.Factory.ConnectCount.Should().Be(0);
        (await s.StoredAsync(ct)).DisplayName.Should().Be("Private");
    }

    [Theory]
    [InlineData("new-password", false)]
    [InlineData(null, true)]
    public async Task Handle_OAuthAccountWithConnectionSettings_Throws(string? password, bool changeServers)
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct, OAuthTestData.OAuthAccount());

        // Act
        var act = async () => await s.Handler.Handle(
            new UpdateAccountCommand(
                s.Account.Id, "Private", TimeSpan.FromMinutes(5), changeServers ? NewImap : null, Password: password), ct);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
        s.Factory.ConnectCount.Should().Be(0);
        (await s.StoredAsync(ct)).DisplayName.Should().Be(s.Account.DisplayName);
    }

    [Fact]
    public async Task Handle_UnknownAccount_Throws()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var s = await Scenario.CreateAsync(ct);

        // Act
        var act = async () => await s.Handler.Handle(
            new UpdateAccountCommand(Guid.NewGuid(), "Work", TimeSpan.FromMinutes(5)), ct);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>
    /// A stored account with its secrets and the handler under test.
    /// </summary>
    private sealed class Scenario : IAsyncDisposable
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="Scenario"/> class.
        /// </summary>
        /// <param name="database">The database holding the account.</param>
        /// <param name="account">The account.</param>
        private Scenario(TestDatabase database, MailAccount account)
        {
            Database = database;

            // A copy of the stored values: the handler changes the instance tracked by its repository.
            Account = account;
            Handler = new UpdateAccountCommandHandler(database.CreateAccountRepository(), Secrets, Factory, Revisions);
        }

        /// <summary>
        /// Gets the database.
        /// </summary>
        public TestDatabase Database { get; }

        /// <summary>
        /// Gets the account as it was before the command.
        /// </summary>
        public MailAccount Account { get; }

        /// <summary>
        /// Gets the secret store.
        /// </summary>
        public InMemorySecretStore Secrets { get; } = new();

        /// <summary>
        /// Gets the connection factory.
        /// </summary>
        public FakeImapSessionFactory Factory { get; } = new();

        /// <summary>
        /// Gets the connection settings revisions.
        /// </summary>
        public AccountConnectionRevisions Revisions { get; } = new();

        /// <summary>
        /// Gets the handler under test.
        /// </summary>
        public UpdateAccountCommandHandler Handler { get; }

        /// <summary>
        /// Gets the stored password.
        /// </summary>
        public string? StoredPassword => Secrets.Values.GetValueOrDefault(AccountSecrets.PasswordKey(Account.Id));

        /// <summary>
        /// Creates a scenario with one stored account.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <param name="account">The account to store; a password account by default.</param>
        /// <returns>The scenario.</returns>
        public static async Task<Scenario> CreateAsync(CancellationToken cancellationToken, MailAccount? account = null)
        {
            var database = await TestDatabase.CreateAsync(cancellationToken);
            account ??= TestData.Account("me@example.com");
            await using (var setup = database.CreateVerifier())
            {
                await setup.Accounts().AddAsync(account, cancellationToken);
            }

            var scenario = new Scenario(database, account);
            scenario.Secrets.Values[AccountSecrets.PasswordKey(account.Id)] = "old-password";
            return scenario;
        }

        /// <summary>
        /// Reads the account through fresh contexts.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The persisted account.</returns>
        public async Task<MailAccount> StoredAsync(CancellationToken cancellationToken)
        {
            await using var verifier = Database.CreateVerifier();
            return (await verifier.Accounts().GetAsync(Account.Id, cancellationToken))!;
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            return Database.DisposeAsync();
        }
    }
}
