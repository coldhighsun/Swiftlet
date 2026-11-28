using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Swiftlet.Infrastructure.Persistence;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for upgrading databases of earlier versions with the columns behind the Trash cleanup.
/// </summary>
public sealed class TrashCleanupSchemaTests
{
    [Fact]
    public async Task InitializeGlobal_AccountsWithoutRetentionColumns_AddsThemSwitchedOffIdempotently()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenAsync(
            """
            CREATE TABLE Accounts (Id TEXT NOT NULL PRIMARY KEY, DisplayName TEXT, EmailAddress TEXT, AuthMethod INTEGER, CreatedAt INTEGER NOT NULL DEFAULT 0);
            INSERT INTO Accounts (Id, DisplayName, EmailAddress, AuthMethod) VALUES ('a', 'me', 'me@example.com', 0);
            """,
            ct);
        await using var context = new GlobalDbContext(new DbContextOptionsBuilder<GlobalDbContext>().UseSqlite(connection).Options);

        // Act
        await SqliteSchemaInitializer.InitializeGlobalAsync(context, ct);
        await SqliteSchemaInitializer.InitializeGlobalAsync(context, ct);

        // Assert
        var off = await context.Database
            .SqlQueryRaw<int>(
                "SELECT COUNT(*) AS Value FROM Accounts WHERE TrashRetentionDays IS NULL AND TrashCleanupEnabledAt IS NULL")
            .SingleAsync(ct);
        off.Should().Be(1);
    }

    [Fact]
    public async Task InitializeAccount_FoldersWithoutCheckpointColumn_AddsItEmptyIdempotently()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await OpenAsync(
            """
            CREATE TABLE Folders (Id TEXT NOT NULL PRIMARY KEY, AccountId TEXT NOT NULL, Name TEXT NOT NULL, FullName TEXT NOT NULL, Role INTEGER NOT NULL, UidValidity INTEGER NULL);
            CREATE TABLE Messages (Id TEXT NOT NULL PRIMARY KEY, FolderId TEXT NULL, Uid INTEGER NULL, Subject TEXT NOT NULL, FromName TEXT NOT NULL, FromAddress TEXT NOT NULL, BodyText TEXT NOT NULL, MessageIdHeader TEXT NULL, IsRead INTEGER NOT NULL, IsFlagged INTEGER NOT NULL);
            INSERT INTO Folders (Id, AccountId, Name, FullName, Role) VALUES ('11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222', 'Trash', 'Trash', 4);
            """,
            ct);
        await using var context = new AccountDbContext(new DbContextOptionsBuilder<AccountDbContext>().UseSqlite(connection).Options);

        // Act
        await SqliteSchemaInitializer.InitializeAccountAsync(context, ct);
        await SqliteSchemaInitializer.InitializeAccountAsync(context, ct);

        // Assert
        var folder = await context.Folders.AsNoTracking().SingleAsync(ct);
        folder.TrashCheckpoints.Should().BeEmpty();
    }

    /// <summary>
    /// Opens an in-memory database prepared like one of an earlier version.
    /// </summary>
    /// <param name="script">The statements creating it.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The open connection.</returns>
    private static async Task<SqliteConnection> OpenAsync(string script, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = script;
        await command.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }
}
