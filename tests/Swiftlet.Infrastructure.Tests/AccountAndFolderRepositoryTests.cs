using AwesomeAssertions;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;
using Swiftlet.Infrastructure.Persistence.Repositories;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="AccountRepository"/> and <see cref="FolderRepository"/>.
/// </summary>
public class AccountAndFolderRepositoryTests
{
    /// <summary>
    /// Verifies an account with owned server settings and TimeSpan round-trips.
    /// </summary>
    [Fact]
    public async Task AddAsync_Account_RoundTripsAllValues()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var account = MailAccount.Create(
            "Me",
            "me@example.com",
            AuthMethod.OAuth2,
            new ServerEndpoint("imap.example.com", 993, true),
            new ServerEndpoint("smtp.example.com", 587, false),
            DateTimeOffset.UtcNow);
        account.ChangeSyncInterval(TimeSpan.FromMinutes(15));
        await new AccountRepository(db.Context).AddAsync(account, ct);

        await using var verifyContext = db.CreateContext();
        var loaded = await new AccountRepository(verifyContext).GetAsync(account.Id, ct);

        loaded.Should().NotBeNull();
        loaded!.AuthMethod.Should().Be(AuthMethod.OAuth2);
        loaded.Imap.Should().Be(new ServerEndpoint("imap.example.com", 993, true));
        loaded.Smtp.Should().Be(new ServerEndpoint("smtp.example.com", 587, false));
        loaded.SyncInterval.Should().Be(TimeSpan.FromMinutes(15));
    }

    /// <summary>
    /// Verifies duplicate email addresses are rejected by the database.
    /// </summary>
    [Fact]
    public async Task AddAsync_DuplicateEmail_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var repository = new AccountRepository(db.Context);
        var imap = new ServerEndpoint("imap.example.com", 993, true);
        var smtp = new ServerEndpoint("smtp.example.com", 465, true);
        await repository.AddAsync(MailAccount.Create("A", "dup@example.com", AuthMethod.Password, imap, smtp, DateTimeOffset.UtcNow), ct);

        var act = async () => await repository.AddAsync(
            MailAccount.Create("B", "dup@example.com", AuthMethod.Password, imap, smtp, DateTimeOffset.UtcNow), ct);

        await act.Should().ThrowAsync<Exception>();
    }

    /// <summary>
    /// Verifies folder UIDVALIDITY changes are persisted via <c>SaveChangesAsync</c>.
    /// </summary>
    [Fact]
    public async Task SaveChangesAsync_UpdatedUidValidity_IsPersisted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        var (account, folder) = await db.SeedAccountAsync(ct);
        var repository = new FolderRepository(db.Context);

        folder.UpdateUidValidity(42);
        await repository.SaveChangesAsync(ct);

        await using var verifyContext = db.CreateContext();
        var folders = await new FolderRepository(verifyContext).GetByAccountAsync(account.Id, ct);
        folders.Should().ContainSingle().Which.UidValidity.Should().Be(42u);
    }
}
