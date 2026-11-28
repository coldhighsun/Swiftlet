using AwesomeAssertions;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Accounts;
using Swiftlet.Application.Sending;
using Swiftlet.Application.Sync;
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
    /// Verifies only a typed name is marked as the user's own, and a blank one falls back to the local part.
    /// </summary>
    /// <param name="typed">The name sent with the command.</param>
    /// <param name="expected">The stored name.</param>
    /// <param name="custom">Whether the stored name counts as typed by the user.</param>
    [Theory]
    [InlineData("Work", "Work", true)]
    [InlineData("", "me", false)]
    [InlineData("   ", "me", false)]
    public async Task AddAccount_DisplayName_RemembersWhetherItWasTyped(string typed, string expected, bool custom)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var handler = new AddAccountCommandHandler(
            db.CreateAccountRepository(), new InMemorySecretStore(), new FakeImapSessionFactory(), TimeProvider.System);

        var id = await handler.Handle(new AddAccountCommand(typed, "me@gmail.com", "pw"), ct);

        await using var verify = db.CreateVerifier();
        var saved = await verify.Accounts().GetAsync(id, ct);
        saved!.DisplayName.Should().Be(expected);
        saved.HasCustomDisplayName.Should().Be(custom);
    }

    /// <summary>
    /// Verifies a chosen color is stored, and an account added without one gets a color no other account shows.
    /// </summary>
    [Fact]
    public async Task AddAccount_Color_KeepsTheChosenOneOrPicksAnUnusedOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var handler = new AddAccountCommandHandler(
            db.CreateAccountRepository(), new InMemorySecretStore(), new FakeImapSessionFactory(), TimeProvider.System);

        var chosen = await handler.Handle(new AddAccountCommand("", "me@gmail.com", "pw", Color: "#C2185B"), ct);
        for (var i = 1; i < AccountColors.Palette.Count; i++)
        {
            // Own endpoint instances: one context cannot track a preset's instance for two accounts.
            var server = new ServerEndpoint("mail.example.com", 993, true);
            await handler.Handle(new AddAccountCommand("", $"me{i}@gmail.com", "pw", server, server with { Port = 465 }), ct);
        }

        await using var verify = db.CreateVerifier();
        var accounts = await verify.Accounts().GetAllAsync(ct);
        accounts.Single(a => a.Id == chosen).Color.Should().Be("#C2185B");
        accounts.Select(a => a.Color).Should().BeEquivalentTo(AccountColors.Palette);
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
        var handler = new SendMessageCommandHandler(repository, sender, new SyncEvents(), db.CreateContactRepository());
        var message = new OutgoingMessage { To = ["you@example.com"], Subject = "Hi", TextBody = "Body" };

        await handler.Handle(new SendMessageCommand(account.Id, message, explicitSave), ct);

        var sent = sender.Sent.Should().ContainSingle().Subject;
        sent.SaveToSent.Should().Be(expected);
        sent.Account.Id.Should().Be(account.Id);
    }

    /// <summary>
    /// Verifies a delivered message is announced with its account.
    /// </summary>
    [Fact]
    public async Task SendMessage_Delivered_RaisesMessageSent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var repository = db.CreateAccountRepository();
        var account = TestData.Account("me@qq.com");
        await repository.AddAsync(account, ct);
        var events = new SyncEvents();
        var announced = new List<Guid>();
        events.MessageSent += (_, e) => announced.Add(e.AccountId);
        var handler = new SendMessageCommandHandler(repository, new RecordingMailSender(), events, db.CreateContactRepository());

        await handler.Handle(new SendMessageCommand(account.Id, new OutgoingMessage { To = ["you@example.com"] }), ct);

        announced.Should().Equal(account.Id);
    }

    /// <summary>
    /// Verifies a failed send is not announced.
    /// </summary>
    [Fact]
    public async Task SendMessage_SenderFails_RaisesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var repository = db.CreateAccountRepository();
        var account = TestData.Account("me@qq.com");
        await repository.AddAsync(account, ct);
        var events = new SyncEvents();
        var announced = 0;
        events.MessageSent += (_, _) => announced++;
        var sender = new RecordingMailSender { Failure = new IOException("offline") };
        var handler = new SendMessageCommandHandler(repository, sender, events, db.CreateContactRepository());

        var act = async () => await handler.Handle(new SendMessageCommand(account.Id, new OutgoingMessage()), ct);

        await act.Should().ThrowAsync<IOException>();
        announced.Should().Be(0);
    }

    /// <summary>
    /// Verifies sending from an unknown account fails.
    /// </summary>
    [Fact]
    public async Task SendMessage_UnknownAccount_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var handler = new SendMessageCommandHandler(db.CreateAccountRepository(), new RecordingMailSender(), new SyncEvents(), db.CreateContactRepository());

        var act = async () => await handler.Handle(new SendMessageCommand(Guid.NewGuid(), new OutgoingMessage()), ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
