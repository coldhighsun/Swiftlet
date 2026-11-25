using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Swiftlet.Infrastructure.Persistence;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="SqliteSchemaInitializer"/>.
/// </summary>
public class SchemaInitializerTests
{
    /// <summary>
    /// Counts the <c>IsFlagSyncPending</c> columns of the Messages table.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of matching columns.</returns>
    private static async Task<int> CountPendingColumnsAsync(SwiftletDbContext context, CancellationToken cancellationToken)
    {
        return await context.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM pragma_table_info('Messages') WHERE name = 'IsFlagSyncPending'")
            .SingleAsync(cancellationToken);
    }

    /// <summary>
    /// Verifies a database created by the previous version gains the column and keeps its rows.
    /// </summary>
    [Fact]
    public async Task Initialize_ExistingDatabaseWithoutColumn_AddsColumn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(ct);
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText =
                """
                CREATE TABLE Messages (
                    Id TEXT NOT NULL PRIMARY KEY,
                    Subject TEXT NOT NULL,
                    FromName TEXT NOT NULL,
                    FromAddress TEXT NOT NULL,
                    BodyText TEXT NOT NULL,
                    IsRead INTEGER NOT NULL,
                    IsFlagged INTEGER NOT NULL);
                INSERT INTO Messages (Id, Subject, FromName, FromAddress, BodyText, IsRead, IsFlagged)
                VALUES ('11111111-1111-1111-1111-111111111111', 'old', 'A', 'a@example.com', 'body', 1, 0);
                """;
            await setup.ExecuteNonQueryAsync(ct);
        }

        var options = new DbContextOptionsBuilder<SwiftletDbContext>().UseSqlite(connection).Options;
        await using var context = new SwiftletDbContext(options);
        (await CountPendingColumnsAsync(context, ct)).Should().Be(0);

        await SqliteSchemaInitializer.InitializeAsync(context, ct);

        (await CountPendingColumnsAsync(context, ct)).Should().Be(1);
        var pending = await context.Database
            .SqlQueryRaw<int>("SELECT IsFlagSyncPending AS Value FROM Messages")
            .SingleAsync(ct);
        pending.Should().Be(0);
        var ftsTables = await context.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sqlite_master WHERE name = 'MessageSearch'")
            .SingleAsync(ct);
        ftsTables.Should().Be(1);
    }

    /// <summary>
    /// Verifies running the initializer repeatedly neither fails nor duplicates the column.
    /// </summary>
    [Fact]
    public async Task Initialize_CalledTwice_IsIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);

        await SqliteSchemaInitializer.InitializeAsync(db.Context, ct);

        (await CountPendingColumnsAsync(db.Context, ct)).Should().Be(1);
    }
}
