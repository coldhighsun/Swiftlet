using System.Text;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Swiftlet.Infrastructure.Persistence;
using Swiftlet.Infrastructure.Persistence.Encryption;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="DatabaseEncryptionMigrator"/> against real files.
/// </summary>
public sealed class DatabaseEncryptionMigratorTests : IDisposable
{
    /// <summary>
    /// The temporary directory of one test.
    /// </summary>
    private readonly EncryptionTestDirectory _directory = new();

    /// <summary>
    /// Creates an account database with one searchable message, as the application leaves it.
    /// </summary>
    /// <returns>The database path.</returns>
    private async Task<string> CreateAccountDatabaseAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = _directory.File("account.db");
        var options = new DbContextOptionsBuilder<AccountDbContext>()
            .UseSqlite(SqliteConnectionStrings.Build(path, null, pooling: false))
            .Options;
        await using (var context = new AccountDbContext(options))
        {
            await SqliteSchemaInitializer.InitializeAccountAsync(context, ct);
        }

        EncryptionTestDirectory.Scalar(
            path,
            null,
            "INSERT INTO MessageSearch(rowid, Subject, FromName, FromAddress, BodyText) VALUES (1, '季度会议 SECRETMARKER', 'a', 'b', 'c')");
        EncryptionTestDirectory.Scalar(path, null, "PRAGMA user_version = 3");
        return path;
    }

    /// <summary>
    /// Verifies conversion keeps the data, the full-text index, the pending tasks and the journal mode.
    /// </summary>
    [Fact]
    public async Task EncryptAsync_PlainDatabase_KeepsDataSearchAndUserVersion()
    {
        var path = await CreateAccountDatabaseAsync();
        var tables = EncryptionTestDirectory.Scalar(path, null, "SELECT COUNT(*) FROM sqlite_master");

        await new DatabaseEncryptionMigrator().EncryptAsync(path, EncryptionTestDirectory.Key, TestContext.Current.CancellationToken);

        const string key = EncryptionTestDirectory.Key;
        EncryptionTestDirectory.Scalar(path, key, "SELECT COUNT(*) FROM sqlite_master").Should().Be(tables);
        EncryptionTestDirectory.Scalar(path, key, "SELECT COUNT(*) FROM MessageSearch WHERE MessageSearch MATCH '\"季度会议\"'").Should().Be(1L);
        EncryptionTestDirectory.Scalar(path, key, "PRAGMA user_version").Should().Be(3L);
        EncryptionTestDirectory.Scalar(path, key, "PRAGMA journal_mode").Should().Be("wal");
    }

    /// <summary>
    /// Verifies no file of the converted database still holds readable text.
    /// </summary>
    [Fact]
    public async Task EncryptAsync_PlainDatabase_LeavesNoPlainText()
    {
        var path = _directory.File("items.db");
        EncryptionTestDirectory.CreateDatabase(path, null, rows: 500);

        await new DatabaseEncryptionMigrator().EncryptAsync(path, EncryptionTestDirectory.Key, TestContext.Current.CancellationToken);

        foreach (var file in Directory.GetFiles(_directory.Root, "items.db*"))
        {
            Encoding.Latin1.GetString(File.ReadAllBytes(file)).Should().NotContain(EncryptionTestDirectory.Marker);
        }

        EncryptionTestDirectory.Scalar(path, EncryptionTestDirectory.Key, "SELECT COUNT(*) FROM Items").Should().Be(500L);
    }

    /// <summary>
    /// Verifies a full disk is noticed before the database is touched.
    /// </summary>
    [Fact]
    public async Task EncryptAsync_NotEnoughSpace_LeavesFileUnchanged()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = _directory.File("items.db");
        EncryptionTestDirectory.CreateDatabase(path, null);
        var before = await File.ReadAllBytesAsync(path, ct);

        var act = async () => await new DatabaseEncryptionMigrator(_ => 0).EncryptAsync(path, EncryptionTestDirectory.Key, ct);

        (await act.Should().ThrowAsync<DatabaseConversionException>()).Which.Failure.Should().Be(DatabaseConversionFailure.NotEnoughSpace);
        (await File.ReadAllBytesAsync(path, ct)).Should().Equal(before);
    }

    /// <summary>
    /// Verifies a database another process is using is reported and stays a readable plain database.
    /// </summary>
    [Fact]
    public async Task EncryptAsync_OpenElsewhere_ThrowsInUseAndKeepsDatabase()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = _directory.File("items.db");
        EncryptionTestDirectory.CreateDatabase(path, null, rows: 10);
        EncryptionTestDirectory.Scalar(path, null, "PRAGMA journal_mode=WAL");
        await using (var other = new SqliteConnection(SqliteConnectionStrings.Build(path, null, pooling: false)))
        {
            await other.OpenAsync(ct);
            await using var transaction = other.BeginTransaction(deferred: true);
            await using var command = other.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT COUNT(*) FROM Items";
            await command.ExecuteScalarAsync(ct);

            var act = async () => await new DatabaseEncryptionMigrator().EncryptAsync(path, EncryptionTestDirectory.Key, ct);

            (await act.Should().ThrowAsync<DatabaseConversionException>()).Which.Failure.Should().Be(DatabaseConversionFailure.InUse);
        }

        (await DatabaseProbe.ProbeAsync(path, EncryptionTestDirectory.Key, ct)).Should().Be(DatabaseFileState.Plain);
        EncryptionTestDirectory.Scalar(path, null, "SELECT COUNT(*) FROM Items").Should().Be(10L);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _directory.Dispose();
    }
}
