using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Swiftlet.Domain.Accounts;
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
    private static async Task<int> CountPendingColumnsAsync(DbContext context, CancellationToken cancellationToken)
    {
        return await context.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM pragma_table_info('Messages') WHERE name = 'IsFlagSyncPending'")
            .SingleAsync(cancellationToken);
    }

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
    /// Verifies a database created by the previous version gains the column and keeps its rows.
    /// </summary>
    [Fact]
    public async Task InitializeAccount_ExistingDatabaseWithoutColumn_AddsColumn()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(ct);
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText =
                """
                CREATE TABLE Folders (Id TEXT NOT NULL PRIMARY KEY);
                CREATE TABLE Messages (
                    Id TEXT NOT NULL PRIMARY KEY,
                    FolderId TEXT NULL,
                    Uid INTEGER NULL,
                    Subject TEXT NOT NULL,
                    FromName TEXT NOT NULL,
                    FromAddress TEXT NOT NULL,
                    BodyText TEXT NOT NULL,
                    MessageIdHeader TEXT NULL,
                    IsRead INTEGER NOT NULL,
                    IsFlagged INTEGER NOT NULL);
                INSERT INTO Messages (Id, Subject, FromName, FromAddress, BodyText, IsRead, IsFlagged)
                VALUES ('11111111-1111-1111-1111-111111111111', 'old', 'A', 'a@example.com', 'body', 1, 0);
                """;
            await setup.ExecuteNonQueryAsync(ct);
        }

        var options = new DbContextOptionsBuilder<AccountDbContext>().UseSqlite(connection).Options;
        await using var context = new AccountDbContext(options);
        (await CountPendingColumnsAsync(context, ct)).Should().Be(0);

        await SqliteSchemaInitializer.InitializeAccountAsync(context, ct);

        (await CountPendingColumnsAsync(context, ct)).Should().Be(1);
        var deletion = await context.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM pragma_table_info('Messages') WHERE name = 'IsDeletePending' UNION ALL SELECT COUNT(*) FROM pragma_table_info('Folders') WHERE name = 'EmptyThroughUid'")
            .ToListAsync(ct);
        deletion.Should().Equal(1, 1);
        var moveColumns = await context.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM pragma_table_info('Messages') WHERE name = 'PendingMoveFolderId'")
            .SingleAsync(ct);
        moveColumns.Should().Be(1);
        var priority = await context.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM pragma_table_info('Messages') WHERE name = 'AiPriority' AND \"notnull\" = 0")
            .SingleAsync(ct);
        priority.Should().Be(1);
        (await context.Database.SqlQueryRaw<int>("SELECT IsDeletePending AS Value FROM Messages").SingleAsync(ct)).Should().Be(0);
        var pending = await context.Database
            .SqlQueryRaw<int>("SELECT IsFlagSyncPending AS Value FROM Messages")
            .SingleAsync(ct);
        pending.Should().Be(0);
        (await CountTablesAsync(context, "MessageSearch", ct)).Should().Be(1);
    }

    /// <summary>
    /// Verifies running the account initializer repeatedly neither fails nor duplicates the column.
    /// </summary>
    [Fact]
    public async Task InitializeAccount_CalledTwice_IsIdempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        await using var context = await db.Factory.CreateAsync(Guid.NewGuid(), ct);

        await SqliteSchemaInitializer.InitializeAccountAsync(context, ct);

        (await CountPendingColumnsAsync(context, ct)).Should().Be(1);
    }

    /// <summary>
    /// Verifies the global store holds the accounts, labels and label links, but no mail tables.
    /// </summary>
    [Fact]
    public async Task InitializeGlobal_NewDatabase_HasOnlyGlobalTables()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);

        (await CountTablesAsync(db.Context, "Accounts", ct)).Should().Be(1);
        (await CountTablesAsync(db.Context, "Labels", ct)).Should().Be(1);
        (await CountTablesAsync(db.Context, "MessageLabels", ct)).Should().Be(1);
        (await CountTablesAsync(db.Context, "Messages", ct)).Should().Be(0);
        (await CountTablesAsync(db.Context, "Folders", ct)).Should().Be(0);
        (await CountTablesAsync(db.Context, "Attachments", ct)).Should().Be(0);
        (await CountTablesAsync(db.Context, "MessageSearch", ct)).Should().Be(0);
    }

    /// <summary>
    /// Verifies a new global store contains the remote image rules table.
    /// </summary>
    [Fact]
    public async Task InitializeGlobal_NewDatabase_HasRemoteImageRulesTable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);

        (await CountTablesAsync(db.Context, "RemoteImageRules", ct)).Should().Be(1);
    }

    /// <summary>
    /// Verifies a global store created before the rules existed gains the table, and a second run keeps its rows.
    /// </summary>
    [Fact]
    public async Task InitializeGlobal_ExistingDatabaseWithoutTable_AddsTableIdempotently()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(ct);
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText =
                "CREATE TABLE Accounts (Id TEXT NOT NULL PRIMARY KEY, DisplayName TEXT, EmailAddress TEXT, AuthMethod INTEGER, CreatedAt INTEGER NOT NULL DEFAULT 0);";
            await setup.ExecuteNonQueryAsync(ct);
        }

        var options = new DbContextOptionsBuilder<GlobalDbContext>().UseSqlite(connection).Options;
        await using var context = new GlobalDbContext(options);
        (await CountTablesAsync(context, "RemoteImageRules", ct)).Should().Be(0);

        await SqliteSchemaInitializer.InitializeGlobalAsync(context, ct);

        (await CountTablesAsync(context, "RemoteImageRules", ct)).Should().Be(1);
        await context.Database.ExecuteSqlRawAsync("INSERT INTO RemoteImageRules (Rule, AddedAt) VALUES ('shop.com', 1)", ct);

        await SqliteSchemaInitializer.InitializeGlobalAsync(context, ct);

        var rows = await context.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM RemoteImageRules").SingleAsync(ct);
        rows.Should().Be(1);
    }

    /// <summary>
    /// Verifies a global store of an earlier version gains the color column once, with no color chosen.
    /// </summary>
    [Fact]
    public async Task InitializeGlobal_AccountsWithoutColorColumn_AddsColumnIdempotently()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(ct);
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText =
                """
                CREATE TABLE Accounts (Id TEXT NOT NULL PRIMARY KEY, DisplayName TEXT, EmailAddress TEXT, AuthMethod INTEGER, CreatedAt INTEGER NOT NULL DEFAULT 0);
                INSERT INTO Accounts (Id, DisplayName, EmailAddress, AuthMethod) VALUES ('a', 'me', 'me@example.com', 0);
                """;
            await setup.ExecuteNonQueryAsync(ct);
        }

        var options = new DbContextOptionsBuilder<GlobalDbContext>().UseSqlite(connection).Options;
        await using var context = new GlobalDbContext(options);

        await SqliteSchemaInitializer.InitializeGlobalAsync(context, ct);
        var unchosen = await context.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM Accounts WHERE Color IS NULL")
            .SingleAsync(ct);
        await context.Database.ExecuteSqlRawAsync("UPDATE Accounts SET Color = '#C2185B'", ct);
        await SqliteSchemaInitializer.InitializeGlobalAsync(context, ct);

        unchosen.Should().Be(1);
        var kept = await context.Database.SqlQueryRaw<string>("SELECT Color AS Value FROM Accounts").SingleAsync(ct);
        kept.Should().Be("#C2185B");
    }

    /// <summary>
    /// Verifies a global store of an earlier version gains the archive folder column once, with no folder chosen.
    /// </summary>
    [Fact]
    public async Task InitializeGlobal_AccountsWithoutArchiveFolderColumn_AddsColumnIdempotently()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(ct);
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText =
                """
                CREATE TABLE Accounts (Id TEXT NOT NULL PRIMARY KEY, DisplayName TEXT, EmailAddress TEXT, AuthMethod INTEGER, CreatedAt INTEGER NOT NULL DEFAULT 0);
                INSERT INTO Accounts (Id, DisplayName, EmailAddress, AuthMethod) VALUES ('a', 'me', 'me@example.com', 0);
                """;
            await setup.ExecuteNonQueryAsync(ct);
        }

        var options = new DbContextOptionsBuilder<GlobalDbContext>().UseSqlite(connection).Options;
        await using var context = new GlobalDbContext(options);

        await SqliteSchemaInitializer.InitializeGlobalAsync(context, ct);
        await SqliteSchemaInitializer.InitializeGlobalAsync(context, ct);

        var unchosen = await context.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM Accounts WHERE ArchiveFolderName IS NULL")
            .SingleAsync(ct);
        unchosen.Should().Be(1);
    }

    /// <summary>
    /// Verifies accounts of an earlier version count as named by the user only for a password account whose name is not
    /// the local part of its address, and that a second run leaves the marks alone.
    /// </summary>
    [Fact]
    public async Task InitializeGlobal_AccountsWithoutCustomNameColumn_MarksOnlyTypedNames()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(ct);
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"""
                CREATE TABLE Accounts (Id TEXT NOT NULL PRIMARY KEY, DisplayName TEXT, EmailAddress TEXT, AuthMethod INTEGER, CreatedAt INTEGER NOT NULL DEFAULT 0);
                INSERT INTO Accounts (Id, DisplayName, EmailAddress, AuthMethod) VALUES ('typed', 'Work', 'me@example.com', {(int)AuthMethod.Password});
                INSERT INTO Accounts (Id, DisplayName, EmailAddress, AuthMethod) VALUES ('filled', 'me', 'me@example.org', {(int)AuthMethod.Password});
                INSERT INTO Accounts (Id, DisplayName, EmailAddress, AuthMethod) VALUES ('oauth', 'Me Myself', 'me@gmail.com', {(int)AuthMethod.OAuth2});
                """);
            await setup.ExecuteNonQueryAsync(ct);
        }

        var options = new DbContextOptionsBuilder<GlobalDbContext>().UseSqlite(connection).Options;
        await using var context = new GlobalDbContext(options);

        await SqliteSchemaInitializer.InitializeGlobalAsync(context, ct);
        await context.Database.ExecuteSqlRawAsync("UPDATE Accounts SET DisplayName = 'Other' WHERE Id = 'filled'", ct);
        await SqliteSchemaInitializer.InitializeGlobalAsync(context, ct);

        var marked = await context.Database
            .SqlQueryRaw<string>("SELECT Id AS Value FROM Accounts WHERE HasCustomDisplayName = 1")
            .ToListAsync(ct);
        marked.Should().Equal("typed");
    }

    /// <summary>
    /// Verifies an account database holds the mail tables and the full-text index, but no global tables.
    /// </summary>
    [Fact]
    public async Task InitializeAccount_NewDatabase_HasOnlyMailTables()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        await using var context = await db.Factory.CreateAsync(Guid.NewGuid(), ct);

        (await CountTablesAsync(context, "Folders", ct)).Should().Be(1);
        (await CountTablesAsync(context, "Messages", ct)).Should().Be(1);
        (await CountTablesAsync(context, "Attachments", ct)).Should().Be(1);
        (await CountTablesAsync(context, "MessageSearch", ct)).Should().Be(1);
        (await CountTablesAsync(context, "Accounts", ct)).Should().Be(0);
        (await CountTablesAsync(context, "Labels", ct)).Should().Be(0);
        (await CountTablesAsync(context, "MessageLabels", ct)).Should().Be(0);
        (await CountTablesAsync(context, "RemoteImageRules", ct)).Should().Be(0);
    }

    /// <summary>
    /// Verifies a database created before the Message-ID index existed gains it, and a repeated run does not fail.
    /// </summary>
    [Fact]
    public async Task InitializeAccount_ExistingDatabaseWithoutMessageIdIndex_AddsIndexIdempotently()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(ct);
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText =
                """
                CREATE TABLE Folders (Id TEXT NOT NULL PRIMARY KEY);
                CREATE TABLE Messages (
                    Id TEXT NOT NULL PRIMARY KEY,
                    FolderId TEXT NULL,
                    Uid INTEGER NULL,
                    Subject TEXT NOT NULL,
                    FromName TEXT NOT NULL,
                    FromAddress TEXT NOT NULL,
                    BodyText TEXT NOT NULL,
                    MessageIdHeader TEXT NULL,
                    IsRead INTEGER NOT NULL,
                    IsFlagged INTEGER NOT NULL,
                    IsFlagSyncPending INTEGER NOT NULL DEFAULT 0);
                """;
            await setup.ExecuteNonQueryAsync(ct);
        }

        var options = new DbContextOptionsBuilder<AccountDbContext>().UseSqlite(connection).Options;
        await using var context = new AccountDbContext(options);

        await SqliteSchemaInitializer.InitializeAccountAsync(context, ct);
        await SqliteSchemaInitializer.InitializeAccountAsync(context, ct);

        var count = await context.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'index' AND name = 'IX_Messages_MessageIdHeader'")
            .SingleAsync(ct);
        count.Should().Be(1);
    }

    /// <summary>
    /// Verifies a brand-new account database has the Message-ID index.
    /// </summary>
    [Fact]
    public async Task InitializeAccount_NewDatabase_HasMessageIdIndex()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        await using var context = await db.Factory.CreateAsync(Guid.NewGuid(), ct);

        var count = await context.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'index' AND name = 'IX_Messages_MessageIdHeader'")
            .SingleAsync(ct);

        count.Should().Be(1);
    }

    /// <summary>
    /// Verifies counting unread messages reads only an index, never the rows with their message bodies, in a new database
    /// and in one created by an earlier version.
    /// </summary>
    /// <param name="upgraded">Whether the index is missing first, as in an earlier version.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnreadCountQuery_ReadsOnlyTheIndex(bool upgraded)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await TestDatabase.CreateAsync(ct);
        await using var context = await db.Factory.CreateAsync(Guid.NewGuid(), ct);
        if (upgraded)
        {
            await context.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS IX_Messages_Unread;", ct);
            await SqliteSchemaInitializer.InitializeAccountAsync(context, ct);
        }

        var sql = Swiftlet.Infrastructure.Persistence.Repositories.MessageRepository.UnreadCounts(context).ToQueryString();
        await context.Database.OpenConnectionAsync(ct);
        await using var command = context.Database.GetDbConnection().CreateCommand();
#pragma warning disable CA2100 // The text is the query EF generated from the code above, with no user input.
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
#pragma warning restore CA2100
        var plan = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                plan.Add(reader.GetString(reader.GetOrdinal("detail")));
            }
        }

        plan.Should().ContainSingle().Which.Should().Contain("USING COVERING INDEX IX_Messages_Unread");
    }

    /// <summary>
    /// Verifies accounts of an earlier version are numbered by creation time, start expanded, and that a second run
    /// leaves the order alone.
    /// </summary>
    [Fact]
    public async Task InitializeGlobal_AccountsWithoutOrderColumns_NumbersByCreationAndExpands()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(ct);
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                CREATE TABLE Accounts (Id TEXT NOT NULL PRIMARY KEY, DisplayName TEXT, EmailAddress TEXT, AuthMethod INTEGER, CreatedAt INTEGER);
                INSERT INTO Accounts VALUES ('late', 'L', 'l@example.com', 0, 3000);
                INSERT INTO Accounts VALUES ('early', 'E', 'e@example.com', 0, 1000);
                INSERT INTO Accounts VALUES ('middle', 'M', 'm@example.com', 0, 2000);
                """;
            await setup.ExecuteNonQueryAsync(ct);
        }

        var options = new DbContextOptionsBuilder<GlobalDbContext>().UseSqlite(connection).Options;
        await using var context = new GlobalDbContext(options);

        await SqliteSchemaInitializer.InitializeGlobalAsync(context, ct);
        await context.Database.ExecuteSqlRawAsync("UPDATE Accounts SET SortOrder = 9 WHERE Id = 'early'", ct);
        await SqliteSchemaInitializer.InitializeGlobalAsync(context, ct);

        var order = await context.Database
            .SqlQueryRaw<string>("SELECT Id AS Value FROM Accounts ORDER BY SortOrder")
            .ToListAsync(ct);
        order.Should().Equal("middle", "late", "early");
        var collapsed = await context.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM Accounts WHERE IsExpanded = 0")
            .SingleAsync(ct);
        collapsed.Should().Be(0);
    }
}
