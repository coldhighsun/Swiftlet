using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Accounts;
using Swiftlet.Application.Sync;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Labels;
using Swiftlet.Domain.Messages;
using Swiftlet.Infrastructure.Persistence;
using Swiftlet.Infrastructure.Persistence.Repositories;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="DeleteAccountCommandHandler"/> against real SQLite repositories.
/// </summary>
public class DeleteAccountTests
{
    /// <summary>
    /// A secret store whose removal always fails.
    /// </summary>
    private sealed class FailingRemoveSecretStore : ISecretStore
    {
        /// <inheritdoc />
        public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<string?>(null);
        }

        /// <inheritdoc />
        public Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            return Task.FromException(new IOException("Simulated keychain failure."));
        }
    }

    /// <summary>
    /// Counts the rows of a table through raw SQL.
    /// </summary>
    /// <param name="context">The context to query.</param>
    /// <param name="table">The table name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The row count.</returns>
    private static async Task<int> CountAsync(DbContext context, string table, CancellationToken cancellationToken)
    {
        // The table names are hard-coded literals from this test class, never user input.
#pragma warning disable EF1002
        return await context.Database
            .SqlQueryRaw<int>($"SELECT COUNT(*) AS Value FROM {table}")
            .SingleAsync(cancellationToken);
#pragma warning restore EF1002
    }

    /// <summary>
    /// Adds a message with one attachment and the given label to a folder.
    /// </summary>
    /// <param name="db">The test database.</param>
    /// <param name="account">The account.</param>
    /// <param name="folder">The folder.</param>
    /// <param name="label">The label.</param>
    /// <param name="subject">The subject.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    private static async Task AddMailAsync(
        TestDatabase db,
        MailAccount account,
        MailFolder folder,
        Label label,
        string subject,
        CancellationToken cancellationToken)
    {
        var message = MailMessage.Create(account.Id, folder.Id, 1, subject, "A", "a@example.com", "r", DateTimeOffset.UtcNow, "body");
        message.AddAttachment("a.txt", "text/plain", 3);
        message.AddLabel(label);
        var messages = db.CreateMessageRepository();
        await messages.AddAsync(message, cancellationToken);
        await messages.SaveLabelsAsync(message, cancellationToken);
    }

    /// <summary>
    /// Verifies the account and everything it owns is removed while other accounts stay intact.
    /// </summary>
    [Fact]
    public async Task Handle_AccountWithMail_RemovesAccountFoldersMessagesAttachmentsAndLabelLinks()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (first, firstFolder) = await db.SeedAccountAsync(ct);
        var (second, secondFolder) = await db.SeedAccountAsync(ct);
        var labels = new LabelRepository(db.Context);
        var labelOne = Label.Create("One", "#111111");
        var labelTwo = Label.Create("Two", "#222222");
        await labels.AddAsync(labelOne, ct);
        await labels.AddAsync(labelTwo, ct);
        await AddMailAsync(db, first, firstFolder, labelOne, "deletedsubject", ct);
        await AddMailAsync(db, second, secondFolder, labelTwo, "keptsubject", ct);
        var handler = new DeleteAccountCommandHandler(db.CreateAccountRepository(), new InMemorySecretStore());

        await handler.Handle(new DeleteAccountCommand(first.Id), ct);

        await using var verify = db.CreateVerifier();
        (await verify.Global.Accounts.Select(a => a.Id).ToListAsync(ct)).Should().Equal(second.Id);
        var secondContext = await verify.AccountAsync(second.Id, ct);
        (await secondContext.Folders.Select(f => f.Id).ToListAsync(ct)).Should().Equal(secondFolder.Id);
        (await CountAsync(secondContext, "Messages", ct)).Should().Be(1);
        (await CountAsync(secondContext, "Attachments", ct)).Should().Be(1);
        (await CountAsync(verify.Global, "MessageLabels", ct)).Should().Be(1);
        (await verify.Global.Labels.CountAsync(ct)).Should().Be(2);
        db.Factory.DeletedAccounts.Should().Equal(first.Id);
        db.Factory.OpenAccounts.Should().Equal(second.Id);
        var search = verify.Messages();
        (await search.SearchAsync("deletedsubject", null, 10, ct)).Should().BeEmpty();
        (await search.SearchAsync("keptsubject", null, 10, ct)).Should().HaveCount(1);
    }

    /// <summary>
    /// Verifies a password account has its secret removed.
    /// </summary>
    [Fact]
    public async Task Handle_PasswordAccount_RemovesPasswordSecret()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, _) = await db.SeedAccountAsync(ct);
        var secrets = new InMemorySecretStore();
        secrets.Values[AccountSecrets.PasswordKey(account.Id)] = "pw";
        secrets.Values["unrelated"] = "keep";

        await new DeleteAccountCommandHandler(db.CreateAccountRepository(), secrets)
            .Handle(new DeleteAccountCommand(account.Id), ct);

        secrets.Values.Should().NotContainKey(AccountSecrets.PasswordKey(account.Id));
        secrets.Values.Should().ContainKey("unrelated");
    }

    /// <summary>
    /// Verifies an OAuth account has its token secret removed.
    /// </summary>
    [Fact]
    public async Task Handle_OAuthAccount_RemovesOAuthSecret()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var account = MailAccount.Create(
            "Me",
            "oauth@example.com",
            AuthMethod.OAuth2,
            new ServerEndpoint("imap.example.com", 993, true),
            new ServerEndpoint("smtp.example.com", 465, true),
            DateTimeOffset.UtcNow,
            OAuthProvider.Google);
        await db.CreateAccountRepository().AddAsync(account, ct);
        var secrets = new InMemorySecretStore();
        secrets.Values[AccountSecrets.OAuthKey(account.Id)] = "{}";
        secrets.Values["unrelated"] = "keep";

        await new DeleteAccountCommandHandler(db.CreateAccountRepository(), secrets)
            .Handle(new DeleteAccountCommand(account.Id), ct);

        secrets.Values.Should().NotContainKey(AccountSecrets.OAuthKey(account.Id));
        secrets.Values.Should().ContainKey("unrelated");
        (await db.Context.Accounts.CountAsync(ct)).Should().Be(0);
    }

    /// <summary>
    /// Verifies an unknown id is a no-op.
    /// </summary>
    [Fact]
    public async Task Handle_UnknownAccount_DoesNotThrow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, _) = await db.SeedAccountAsync(ct);
        var secrets = new InMemorySecretStore();
        secrets.Values["other"] = "keep";

        var act = async () => await new DeleteAccountCommandHandler(db.CreateAccountRepository(), secrets)
            .Handle(new DeleteAccountCommand(Guid.NewGuid()), ct);

        await act.Should().NotThrowAsync();
        secrets.Values.Should().HaveCount(1);
        (await db.Context.Accounts.SingleAsync(ct)).Id.Should().Be(account.Id);
    }

    /// <summary>
    /// Verifies a retry for an account whose rows are already gone still removes both possible secrets.
    /// </summary>
    [Fact]
    public async Task Handle_UnknownAccountWithLeftoverSecrets_RemovesBothSecretKeys()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var missingId = Guid.NewGuid();
        var secrets = new InMemorySecretStore();
        secrets.Values[AccountSecrets.PasswordKey(missingId)] = "pw";
        secrets.Values[AccountSecrets.OAuthKey(missingId)] = "{}";
        secrets.Values["unrelated"] = "keep";

        await new DeleteAccountCommandHandler(db.CreateAccountRepository(), secrets)
            .Handle(new DeleteAccountCommand(missingId), ct);

        secrets.Values.Should().ContainSingle().Which.Key.Should().Be("unrelated");
    }

    /// <summary>
    /// Verifies a secret store failure surfaces after the rows are already gone.
    /// </summary>
    [Fact]
    public async Task Handle_SecretRemovalFails_RowsAreAlreadyDeletedAndErrorSurfaces()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, _) = await db.SeedAccountAsync(ct);
        var handler = new DeleteAccountCommandHandler(db.CreateAccountRepository(), new FailingRemoveSecretStore());

        var act = async () => await handler.Handle(new DeleteAccountCommand(account.Id), ct);

        await act.Should().ThrowAsync<IOException>();
        await using var verify = db.CreateVerifier();
        (await verify.Global.Accounts.CountAsync(ct)).Should().Be(0);
        db.Factory.DeletedAccounts.Should().Equal(account.Id);
        db.Factory.OpenAccounts.Should().BeEmpty();
    }

    /// <summary>
    /// Creates a sync service and a scripted server with two folders of messages for an account.
    /// </summary>
    /// <param name="db">The test database.</param>
    /// <param name="factory">The IMAP factory to script.</param>
    /// <returns>The sync service.</returns>
    private static MailSyncService CreateSync(TestDatabase db, FakeImapSessionFactory factory)
    {
        factory.Session.Folders.Add(TestData.Folder("INBOX", FolderRole.Inbox));
        factory.Session.Folders.Add(TestData.Folder("Archive", FolderRole.Other));
        factory.Session.Messages["INBOX"] = [TestData.Remote(1), TestData.Remote(2)];
        factory.Session.Messages["Archive"] = [TestData.Remote(1)];
        return new MailSyncService(
            db.CreateAccountRepository(),
            db.CreateFolderRepository(),
            db.CreateMessageRepository(),
            factory,
            new FakeSyncSettings(),
            new MailSyncOptions(200),
            NullLogger<MailSyncService>.Instance);
    }

    /// <summary>
    /// Deletes an account through a second context; synchronous because the fake hooks are synchronous.
    /// </summary>
    /// <param name="db">The test database.</param>
    /// <param name="accountId">The account identifier.</param>
    private static void DeleteAccountBehindTheScenes(TestDatabase db, Guid accountId)
    {
        var other = db.CreateVerifier();
        try
        {
            // The hook cannot be async, so block on the delete.
            other.Accounts().DeleteAsync(accountId).GetAwaiter().GetResult();
        }
        finally
        {
            other.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    /// <summary>
    /// Asserts the account row, its label links and its database are gone.
    /// </summary>
    /// <param name="db">The test database.</param>
    /// <param name="accountId">The deleted account.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    private static async Task AssertNothingRemainsAsync(TestDatabase db, Guid accountId, CancellationToken cancellationToken)
    {
        await using var verify = db.CreateVerifier();
        (await CountAsync(verify.Global, "Accounts", cancellationToken)).Should().Be(0);
        (await CountAsync(verify.Global, "MessageLabels", cancellationToken)).Should().Be(0);
        db.Factory.DeletedAccounts.Should().Contain(accountId);
        db.Factory.OpenAccounts.Should().BeEmpty();
    }

    /// <summary>
    /// Verifies deleting the account before the first folders are written does not bring the account back.
    /// </summary>
    [Fact]
    public async Task Sync_AccountDeletedDuringSync_LeavesNoAccountRowsBehind()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, _) = await db.SeedAccountAsync(ct);
        var factory = new FakeImapSessionFactory();
        var sync = CreateSync(db, factory);
        var deleted = false;
        factory.Session.OnListFolders = () =>
        {
            deleted = true;
            DeleteAccountBehindTheScenes(db, account.Id);
        };

        // The folders go to the account's own database, which no longer has a foreign key to the account row, so the
        // in-flight run may finish; what matters is that nothing of the account is registered afterwards.
        try
        {
            await sync.SyncAsync(account.Id, ct);
        }
        catch (DbUpdateException)
        {
            // Acceptable too: a failed write of the vanished account's data.
        }

        deleted.Should().BeTrue();
        await AssertNothingRemainsAsync(db, account.Id, ct);
    }

    /// <summary>
    /// Verifies deleting the account while the second folder is fetched leaves no account rows or database behind.
    /// </summary>
    [Fact]
    public async Task Sync_AccountDeletedBetweenFolders_CompletesAndLeavesNoAccountRowsBehind()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, _) = await db.SeedAccountAsync(ct);
        var factory = new FakeImapSessionFactory();
        var sync = CreateSync(db, factory);
        var deleted = false;
        factory.Session.OnFetchNew = folder =>
        {
            if (folder == "INBOX" && !deleted)
            {
                deleted = true;
                DeleteAccountBehindTheScenes(db, account.Id);
            }
        };

        await sync.SyncAsync(account.Id, ct);

        deleted.Should().BeTrue();
        await AssertNothingRemainsAsync(db, account.Id, ct);
    }

    /// <summary>
    /// Verifies the label links of the deleted account are removed while those of other accounts stay.
    /// </summary>
    [Fact]
    public async Task Handle_AccountWithLabelledMail_KeepsOtherAccountsLabelLinks()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (first, firstFolder) = await db.SeedAccountAsync(ct);
        var (second, secondFolder) = await db.SeedAccountAsync(ct);
        var label = Label.Create("Shared", "#111111");
        await db.CreateLabelRepository().AddAsync(label, ct);
        await AddMailAsync(db, first, firstFolder, label, "one", ct);
        await AddMailAsync(db, second, secondFolder, label, "two", ct);

        await new DeleteAccountCommandHandler(db.CreateAccountRepository(), new InMemorySecretStore())
            .Handle(new DeleteAccountCommand(first.Id), ct);

        await using var verify = db.CreateVerifier();
        var links = await verify.Global.Database
            .SqlQueryRaw<string>("SELECT AccountId AS Value FROM MessageLabels")
            .ToListAsync(ct);
        links.Should().ContainSingle().Which.Should().BeEquivalentTo(second.Id.ToString().ToUpperInvariant(), o => o.IgnoringCase());
        (await verify.Messages().GetByLabelAsync(label.Id, 0, 10, ct)).Should().ContainSingle().Which.AccountId.Should().Be(second.Id);
    }
}
