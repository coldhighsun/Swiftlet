using System.Text;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Contacts;
using Swiftlet.Application.Sending;
using Swiftlet.Application.Sync;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Contacts;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Messages;
using Swiftlet.Infrastructure.Persistence;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests the address book against a real SQLite database: storage, collected addresses, commands and file transfer.
/// </summary>
public sealed class ContactTests
{
    /// <summary>
    /// The token of the running test.
    /// </summary>
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Verifies a saved contact and its lists are read back by another context.
    /// </summary>
    [Fact]
    public async Task SaveContact_NewAndChanged_IsPersisted()
    {
        await using var db = await TestDatabase.CreateAsync(Token);
        var handler = new SaveContactCommandHandler(db.CreateContactRepository());

        var id = await handler.Handle(new SaveContactCommand(null, Details("张三", "a@x.com", "b@x.com")), Token);
        await handler.Handle(new SaveContactCommand(id, Details("李四", "c@x.com")), Token);

        await using var verifier = db.CreateVerifier();
        var saved = await verifier.Global.Contacts.SingleAsync(Token);
        saved.Id.Should().Be(id);
        (saved.FirstName, saved.LastName).Should().Be((string.Empty, "李四"));
        saved.Emails.Should().Equal("c@x.com");
        saved.Phones.Should().Equal("123");
    }

    /// <summary>
    /// Verifies an address can belong to one contact only, ignoring case.
    /// </summary>
    [Fact]
    public async Task SaveContact_AddressOfAnotherContact_Throws()
    {
        await using var db = await TestDatabase.CreateAsync(Token);
        var handler = new SaveContactCommandHandler(db.CreateContactRepository());
        await handler.Handle(new SaveContactCommand(null, Details("A", "a@x.com")), Token);

        var act = async () => await handler.Handle(new SaveContactCommand(null, Details("B", "A@X.com")), Token);

        (await act.Should().ThrowAsync<DuplicateContactEmailException>()).Which.Address.Should().Be("A@X.com");
    }

    /// <summary>
    /// Verifies a deleted contact is gone.
    /// </summary>
    [Fact]
    public async Task DeleteContact_Existing_RemovesIt()
    {
        await using var db = await TestDatabase.CreateAsync(Token);
        var repository = db.CreateContactRepository();
        var contact = Contact.Create("A", null, ["a@x.com"]);
        await repository.AddRangeAsync([contact], Token);

        await new DeleteContactCommandHandler(repository).Handle(new DeleteContactCommand(contact.Id), Token);

        (await repository.GetAllAsync(Token)).Should().BeEmpty();
    }

    /// <summary>
    /// Verifies recording a recipient again updates the time and keeps a known name.
    /// </summary>
    [Fact]
    public async Task RecordSentRecipients_Again_UpdatesTimeAndKeepsName()
    {
        await using var db = await TestDatabase.CreateAsync(Token);
        var repository = db.CreateContactRepository();
        var accountId = Guid.NewGuid();
        var first = DateTimeOffset.FromUnixTimeMilliseconds(1000);
        var second = DateTimeOffset.FromUnixTimeMilliseconds(2000);

        await repository.RecordSentRecipientsAsync([new SentRecipient(accountId, "a@x.com", "Ann", first)], Token);
        await repository.RecordSentRecipientsAsync([new SentRecipient(accountId, "a@x.com", string.Empty, second)], Token);

        (await repository.GetSentRecipientsAsync(Token)).Should().Equal(new SentRecipient(accountId, "a@x.com", "Ann", second));
    }

    /// <summary>
    /// Verifies collected addresses come from Sent folders and recorded recipients, newest first, without contacts,
    /// dismissed addresses, own addresses or other folders.
    /// </summary>
    [Fact]
    public async Task GetAddressBook_SentMail_CollectsOnlyUnknownRecipients()
    {
        await using var db = await TestDatabase.CreateAsync(Token);
        var (account, inbox) = await db.SeedAccountAsync(Token);
        var sent = MailFolder.Create(account.Id, "Sent", "Sent", FolderRole.Sent);
        await db.CreateFolderRepository().AddAsync(sent, Token);
        var messages = db.CreateMessageRepository();
        var recipients = $"old@x.com, known@x.com, gone@x.com, {account.EmailAddress}, BOTH@x.com";
        await messages.AddAsync(Message(account.Id, sent.Id, 1, recipients, 1000), Token);
        await messages.AddAsync(Message(account.Id, inbox.Id, 1, "inbox-only@x.com", 5000), Token);
        var repository = db.CreateContactRepository();
        await repository.AddRangeAsync([Contact.Create("Known", null, ["Known@x.com"])], Token);
        await repository.DismissAddressAsync("Gone@x.com", Token);
        await repository.DismissAddressAsync("gone@x.com", Token);
        await repository.RecordSentRecipientsAsync(
            [new SentRecipient(account.Id, "both@x.com", "Bo", DateTimeOffset.FromUnixTimeMilliseconds(3000))],
            Token);
        var handler = new GetAddressBookQueryHandler(repository, messages, db.CreateAccountRepository());

        var book = await handler.Handle(new GetAddressBookQuery(), Token);

        book.Contacts.Should().ContainSingle();
        book.Collected.Select(a => (a.Address, a.Name)).Should().Equal(("both@x.com", "Bo"), ("old@x.com", string.Empty));
    }

    /// <summary>
    /// Verifies sending remembers every recipient, including blind copies, with the typed name.
    /// </summary>
    [Fact]
    public async Task SendMessage_Delivered_RemembersRecipients()
    {
        await using var db = await TestDatabase.CreateAsync(Token);
        var (account, _) = await db.SeedAccountAsync(Token);
        var repository = db.CreateContactRepository();
        var handler = new SendMessageCommandHandler(db.CreateAccountRepository(), new RecordingMailSender(), new SyncEvents(), repository);
        var message = new OutgoingMessage
        {
            To = ["\"Doe, John\" <John@x.com>"],
            Cc = ["c@x.com"],
            Bcc = ["john@x.com", "b@x.com"],
            Subject = "s",
            TextBody = "t",
        };

        await handler.Handle(new SendMessageCommand(account.Id, message), Token);

        var remembered = await repository.GetSentRecipientsAsync(Token);
        remembered.Select(r => (r.Address, r.Name))
            .Should().BeEquivalentTo([("john@x.com", "Doe, John"), ("c@x.com", string.Empty), ("b@x.com", string.Empty)]);
    }

    /// <summary>
    /// Verifies a failure to remember the recipients does not fail a delivered message.
    /// </summary>
    [Fact]
    public async Task SendMessage_RememberingFails_StillSucceeds()
    {
        await using var db = await TestDatabase.CreateAsync(Token);
        var (account, _) = await db.SeedAccountAsync(Token);
        await db.Context.Database.ExecuteSqlRawAsync("DROP TABLE SentRecipients;", Token);
        var sender = new RecordingMailSender();
        var handler = new SendMessageCommandHandler(db.CreateAccountRepository(), sender, new SyncEvents(), db.CreateContactRepository());
        var message = new OutgoingMessage { To = ["a@x.com"], Subject = "s", TextBody = "t" };

        var act = async () => await handler.Handle(new SendMessageCommand(account.Id, message), Token);

        await act.Should().NotThrowAsync();
    }

    /// <summary>
    /// Verifies removing an account forgets its recipients but keeps contacts.
    /// </summary>
    [Fact]
    public async Task DeleteAccount_WithRecipients_RemovesThemAndKeepsContacts()
    {
        await using var db = await TestDatabase.CreateAsync(Token);
        var (account, _) = await db.SeedAccountAsync(Token);
        var other = Guid.NewGuid();
        var repository = db.CreateContactRepository();
        await repository.AddRangeAsync([Contact.Create("A", null, ["a@x.com"])], Token);
        await repository.RecordSentRecipientsAsync(
            [
                new SentRecipient(account.Id, "b@x.com", string.Empty, DateTimeOffset.UnixEpoch),
                new SentRecipient(other, "c@x.com", string.Empty, DateTimeOffset.UnixEpoch),
            ],
            Token);

        await db.CreateAccountRepository().DeleteAsync(account.Id, Token);

        (await repository.GetSentRecipientsAsync(Token)).Should().ContainSingle().Which.AccountId.Should().Be(other);
        (await repository.GetAllAsync(Token)).Should().ContainSingle();
    }

    /// <summary>
    /// Verifies importing adds new contacts, only fills in existing ones and counts skipped entries.
    /// </summary>
    [Fact]
    public async Task Import_Csv_AddsMergesAndSkips()
    {
        await using var db = await TestDatabase.CreateAsync(Token);
        var repository = db.CreateContactRepository();
        await repository.AddRangeAsync(
            [Contact.Create("Kept", null, ["a@x.com"]), Contact.Create("Other", null, ["o@x.com"])],
            Token);
        const string csv = "Name,E-mail Address,E-mail 2 Address,Company\n"
            + "Changed,A@x.com,o@x.com,Acme\n"
            + "New,n@x.com,,\n"
            + "New,n@x.com,n2@x.com,\n"
            + "Nobody,,,\n"
            + "Other,o@x.com,,\n";
        var handler = new ImportContactsCommandHandler(repository);

        var result = await handler.Handle(new ImportContactsCommand(Encoding.UTF8.GetBytes(csv), ContactFileFormat.Csv), Token);

        result.Should().Be(new ImportContactsResult(1, 1, 2));
        await using var verifier = db.CreateVerifier();
        var saved = await verifier.Global.Contacts.ToListAsync(Token);
        var kept = saved.Single(c => c.DisplayName == "Kept");
        kept.LastName.Should().BeEmpty();
        kept.Company.Should().Be("Acme");
        kept.Emails.Should().Equal("a@x.com");
        saved.Single(c => c.DisplayName == "New").Emails.Should().Equal("n@x.com", "n2@x.com");
        saved.Should().HaveCount(3);
    }

    /// <summary>
    /// Verifies a file written with the Chinese system code page is decoded.
    /// </summary>
    [Fact]
    public async Task Import_Gb18030File_DecodesNames()
    {
        await using var db = await TestDatabase.CreateAsync(Token);
        var repository = db.CreateContactRepository();
        var encoding = CodePagesEncodingProvider.Instance.GetEncoding(54936)!;

        await new ImportContactsCommandHandler(repository).Handle(
            new ImportContactsCommand(encoding.GetBytes("姓名,邮箱\n张三,a@x.com\n"), ContactFileFormat.Csv),
            Token);

        (await repository.GetAllAsync(Token)).Should().ContainSingle().Which.DisplayName.Should().Be("张三");
    }

    /// <summary>
    /// Verifies an exported file can be imported again.
    /// </summary>
    /// <param name="format">The file format.</param>
    [Theory]
    [InlineData(ContactFileFormat.VCard)]
    [InlineData(ContactFileFormat.Csv)]
    public async Task Export_ThenImport_RestoresContacts(ContactFileFormat format)
    {
        await using var source = await TestDatabase.CreateAsync(Token);
        await source.CreateContactRepository().AddRangeAsync([Contact.Create("三", "张", ["a@x.com"], ["1"], "公司", "备注")], Token);
        var content = await new ExportContactsQueryHandler(source.CreateContactRepository())
            .Handle(new ExportContactsQuery(format), Token);
        await using var target = await TestDatabase.CreateAsync(Token);

        await new ImportContactsCommandHandler(target.CreateContactRepository())
            .Handle(new ImportContactsCommand(content, format), Token);

        var restored = (await target.CreateContactRepository().GetAllAsync(Token)).Should().ContainSingle().Subject;
        restored.DisplayName.Should().Be("张三");
        restored.Phones.Should().Equal("1");
        restored.Note.Should().Be("备注");
    }

    /// <summary>
    /// Verifies a global store of an earlier version gains the address book tables, repeatedly without harm, and that
    /// they work.
    /// </summary>
    [Fact]
    public async Task InitializeGlobal_OlderDatabase_AddsAddressBookTables()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(Token);
        await using var context = new GlobalDbContext(new DbContextOptionsBuilder<GlobalDbContext>().UseSqlite(connection).Options);
        await SqliteSchemaInitializer.InitializeGlobalAsync(context, Token);
        await context.Database.ExecuteSqlRawAsync(
            "DROP TABLE Contacts; DROP TABLE SentRecipients; DROP TABLE DismissedAddresses;",
            Token);

        await SqliteSchemaInitializer.InitializeGlobalAsync(context, Token);
        await SqliteSchemaInitializer.InitializeGlobalAsync(context, Token);

        var repository = new Persistence.Repositories.ContactRepository(context);
        await repository.AddRangeAsync([Contact.Create("A", "B", ["a@x.com"])], Token);
        await repository.RecordSentRecipientsAsync(
            [new SentRecipient(Guid.NewGuid(), "b@x.com", "B", DateTimeOffset.UnixEpoch)],
            Token);
        await repository.DismissAddressAsync("c@x.com", Token);
        context.ChangeTracker.Clear();
        (await repository.GetAllAsync(Token)).Should().ContainSingle();
        (await repository.GetSentRecipientsAsync(Token)).Should().ContainSingle();
        (await repository.GetDismissedAddressesAsync(Token)).Should().Equal("c@x.com");
    }

    /// <summary>
    /// Creates contact details.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <param name="emails">The addresses.</param>
    /// <returns>The details.</returns>
    private static ContactDetails Details(string name, params string[] emails)
    {
        return new ContactDetails(string.Empty, name, emails, ["123"], string.Empty, string.Empty);
    }

    /// <summary>
    /// Creates a cached message.
    /// </summary>
    /// <param name="accountId">The account.</param>
    /// <param name="folderId">The folder.</param>
    /// <param name="uid">The UID.</param>
    /// <param name="recipients">The recipient list.</param>
    /// <param name="sentAt">The time sent, in Unix milliseconds.</param>
    /// <returns>The message.</returns>
    private static MailMessage Message(Guid accountId, Guid folderId, uint uid, string recipients, long sentAt)
    {
        return MailMessage.Create(
            accountId,
            folderId,
            uid,
            "subject",
            "Me",
            "me@x.com",
            recipients,
            DateTimeOffset.FromUnixTimeMilliseconds(sentAt),
            "body");
    }
}
