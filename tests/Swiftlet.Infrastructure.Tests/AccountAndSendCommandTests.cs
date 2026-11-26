using AwesomeAssertions;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Accounts;
using Swiftlet.Application.Sending;
using Swiftlet.Domain.Accounts;
using Swiftlet.Infrastructure.Persistence.Repositories;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for the add-account and send-message command handlers.
/// </summary>
public class AccountAndSendCommandTests
{
    /// <summary>
    /// Verifies a known provider's preset servers are used and the password is stored under the account key.
    /// </summary>
    [Fact]
    public async Task AddAccount_KnownProvider_UsesPresetAndStoresPassword()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var secrets = new InMemorySecretStore();
        var factory = new FakeImapSessionFactory();
        var handler = new AddAccountCommandHandler(db.CreateAccountRepository(), secrets, factory, TimeProvider.System);

        var id = await handler.Handle(new AddAccountCommand("Me", " me@gmail.com ", "app-password"), ct);

        await using var verify = db.CreateVerifier();
        var saved = await verify.Accounts().GetAsync(id, ct);
        saved.Should().NotBeNull();
        saved!.EmailAddress.Should().Be("me@gmail.com");
        saved.Imap.Host.Should().Be("imap.gmail.com");
        secrets.Values[AccountSecrets.PasswordKey(id)].Should().Be("app-password");
        factory.ConnectCount.Should().Be(1);
        factory.Session.Disposed.Should().BeTrue();
    }

    /// <summary>
    /// Verifies an unknown provider requires explicit server settings.
    /// </summary>
    [Fact]
    public async Task AddAccount_UnknownProviderWithoutServers_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var handler = new AddAccountCommandHandler(
            db.CreateAccountRepository(), new InMemorySecretStore(), new FakeImapSessionFactory(), TimeProvider.System);

        var act = async () => await handler.Handle(new AddAccountCommand("Me", "me@corp.example", "pw"), ct);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    /// <summary>
    /// Verifies explicit server settings work for an unknown provider.
    /// </summary>
    [Fact]
    public async Task AddAccount_UnknownProviderWithServers_Succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var handler = new AddAccountCommandHandler(
            db.CreateAccountRepository(), new InMemorySecretStore(), new FakeImapSessionFactory(), TimeProvider.System);
        var imap = new ServerEndpoint("mail.corp.example", 993, true);
        var smtp = new ServerEndpoint("mail.corp.example", 587, false);

        var id = await handler.Handle(new AddAccountCommand("Me", "me@corp.example", "pw", imap, smtp), ct);

        var saved = await db.CreateAccountRepository().GetAsync(id, ct);
        saved!.Smtp.Should().Be(smtp);
    }

    /// <summary>
    /// Verifies a failed login stores nothing: no account row and no leftover password.
    /// </summary>
    [Fact]
    public async Task AddAccount_LoginFails_RemovesPasswordAndSavesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var secrets = new InMemorySecretStore();
        var factory = new FakeImapSessionFactory { Failure = new InvalidOperationException("bad credentials") };
        var handler = new AddAccountCommandHandler(db.CreateAccountRepository(), secrets, factory, TimeProvider.System);

        var act = async () => await handler.Handle(new AddAccountCommand("Me", "me@gmail.com", "wrong"), ct);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("bad credentials");
        secrets.Values.Should().BeEmpty();
        (await db.CreateAccountRepository().GetAllAsync(ct)).Should().BeEmpty();
    }

    /// <summary>
    /// Verifies the same address cannot be added twice, ignoring case.
    /// </summary>
    [Fact]
    public async Task AddAccount_DuplicateEmail_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var handler = new AddAccountCommandHandler(
            db.CreateAccountRepository(), new InMemorySecretStore(), new FakeImapSessionFactory(), TimeProvider.System);
        await handler.Handle(new AddAccountCommand("Me", "me@gmail.com", "pw"), ct);

        var act = async () => await handler.Handle(new AddAccountCommand("Me", "ME@gmail.com", "pw"), ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>
    /// Verifies the default Sent-copy behavior follows the provider, and an explicit value wins.
    /// </summary>
    /// <param name="email">The sending account address.</param>
    /// <param name="explicitSave">The explicit choice, if any.</param>
    /// <param name="expected">The expected value passed to the sender.</param>
    [Theory]
    [InlineData("me@gmail.com", null, false)]
    [InlineData("me@qq.com", null, true)]
    [InlineData("me@corp.example", null, true)]
    [InlineData("me@gmail.com", true, true)]
    [InlineData("me@qq.com", false, false)]
    public async Task SendMessage_SaveToSentChoice_FollowsProviderUnlessExplicit(string email, bool? explicitSave, bool expected)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var repository = db.CreateAccountRepository();
        var account = TestData.Account(email);
        await repository.AddAsync(account, ct);
        var sender = new RecordingMailSender();
        var handler = new SendMessageCommandHandler(repository, sender);
        var message = new OutgoingMessage { To = ["you@example.com"], Subject = "Hi", TextBody = "Body" };

        await handler.Handle(new SendMessageCommand(account.Id, message, explicitSave), ct);

        var sent = sender.Sent.Should().ContainSingle().Subject;
        sent.SaveToSent.Should().Be(expected);
        sent.Account.Id.Should().Be(account.Id);
    }

    /// <summary>
    /// Verifies sending from an unknown account fails.
    /// </summary>
    [Fact]
    public async Task SendMessage_UnknownAccount_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var handler = new SendMessageCommandHandler(db.CreateAccountRepository(), new RecordingMailSender());

        var act = async () => await handler.Handle(new SendMessageCommand(Guid.NewGuid(), new OutgoingMessage()), ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
