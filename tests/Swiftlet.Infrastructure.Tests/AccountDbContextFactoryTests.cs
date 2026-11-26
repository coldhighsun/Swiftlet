using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Swiftlet.Domain.Folders;
using Swiftlet.Infrastructure.Persistence;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="AccountDbContextFactory"/> against real files in a temporary directory.
/// </summary>
public sealed class AccountDbContextFactoryTests : IDisposable
{
    /// <summary>
    /// The temporary directory holding the account databases of one test.
    /// </summary>
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"swiftlet-test-{Guid.NewGuid():N}", "accounts");

    /// <summary>
    /// Counts the tables or virtual tables with a given name.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="table">The table name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>1 if the table exists, otherwise 0.</returns>
    private static async Task<int> CountTablesAsync(DbContext context, string table, CancellationToken cancellationToken)
    {
        return await context.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM sqlite_master WHERE name = {table}")
            .SingleAsync(cancellationToken);
    }

    /// <summary>
    /// Verifies the first use creates the directory and a file named after the account, with the mail schema.
    /// </summary>
    [Fact]
    public async Task CreateAsync_NewAccount_CreatesNamedFileWithSchema()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new AccountDbContextFactory(_directory);
        var accountId = Guid.NewGuid();

        await using (var context = await factory.CreateAsync(accountId, ct))
        {
            (await CountTablesAsync(context, "Messages", ct)).Should().Be(1);
            (await CountTablesAsync(context, "MessageSearch", ct)).Should().Be(1);
            (await context.Database
                .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM pragma_table_info('Messages') WHERE name = 'IsFlagSyncPending'")
                .SingleAsync(ct)).Should().Be(1);
        }

        File.Exists(Path.Combine(_directory, $"{accountId:D}.db")).Should().BeTrue();
    }

    /// <summary>
    /// Verifies two accounts store their data in separate files.
    /// </summary>
    [Fact]
    public async Task CreateAsync_TwoAccounts_KeepDataInSeparateFiles()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new AccountDbContextFactory(_directory);
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        await using (var context = await factory.CreateAsync(first, ct))
        {
            context.Folders.Add(MailFolder.Create(first, "INBOX", "Inbox", FolderRole.Inbox));
            await context.SaveChangesAsync(ct);
        }

        await using (var context = await factory.CreateAsync(second, ct))
        {
            (await context.Folders.CountAsync(ct)).Should().Be(0);
        }

        await using (var context = await factory.CreateAsync(first, ct))
        {
            (await context.Folders.CountAsync(ct)).Should().Be(1);
        }

        Directory.GetFiles(_directory, "*.db").Should().HaveCount(2);
    }

    /// <summary>
    /// Verifies deleting an account removes the database and sidecar files, and a later use starts empty.
    /// </summary>
    [Fact]
    public async Task DeleteAsync_ExistingAccount_RemovesAllFilesAndLaterCreateStartsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new AccountDbContextFactory(_directory);
        var accountId = Guid.NewGuid();
        var path = Path.Combine(_directory, $"{accountId:D}.db");
        await using (var context = await factory.CreateAsync(accountId, ct))
        {
            context.Folders.Add(MailFolder.Create(accountId, "INBOX", "Inbox", FolderRole.Inbox));
            await context.SaveChangesAsync(ct);
        }

        // A rollback journal can be left behind by an interrupted conversion; it holds mail data too.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await File.WriteAllTextAsync($"{path}-journal", "left behind", ct);

        await factory.DeleteAsync(accountId, ct);

        File.Exists($"{path}-journal").Should().BeFalse();
        File.Exists(path).Should().BeFalse();
        File.Exists($"{path}-wal").Should().BeFalse();
        File.Exists($"{path}-shm").Should().BeFalse();
        await using var recreated = await factory.CreateAsync(accountId, ct);
        (await recreated.Folders.CountAsync(ct)).Should().Be(0);
    }

    /// <summary>
    /// Verifies deleting an account that never had a database does nothing.
    /// </summary>
    [Fact]
    public async Task DeleteAsync_UnknownAccount_DoesNotThrow()
    {
        var ct = TestContext.Current.CancellationToken;
        using var factory = new AccountDbContextFactory(_directory);

        var act = async () => await factory.DeleteAsync(Guid.NewGuid(), ct);

        await act.Should().NotThrowAsync();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var root = Path.GetDirectoryName(_directory);
        if (root is not null && Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
