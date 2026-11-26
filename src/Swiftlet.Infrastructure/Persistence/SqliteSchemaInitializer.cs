using Microsoft.EntityFrameworkCore;

namespace Swiftlet.Infrastructure.Persistence;

/// <summary>
/// Creates the database schema and the FTS5 full-text index with its synchronization triggers.
/// </summary>
public static class SqliteSchemaInitializer
{
    /// <summary>
    /// SQL statements creating the external-content FTS5 table (trigram tokenizer, CJK friendly) and its triggers.
    /// </summary>
    private static readonly string[] FullTextStatements =
    [
        """
        CREATE VIRTUAL TABLE IF NOT EXISTS MessageSearch USING fts5(
            Subject, FromName, FromAddress, BodyText,
            content='Messages', tokenize='trigram');
        """,
        """
        CREATE TRIGGER IF NOT EXISTS Messages_ai AFTER INSERT ON Messages BEGIN
            INSERT INTO MessageSearch(rowid, Subject, FromName, FromAddress, BodyText)
            VALUES (new.rowid, new.Subject, new.FromName, new.FromAddress, new.BodyText);
        END;
        """,
        """
        CREATE TRIGGER IF NOT EXISTS Messages_ad AFTER DELETE ON Messages BEGIN
            INSERT INTO MessageSearch(MessageSearch, rowid, Subject, FromName, FromAddress, BodyText)
            VALUES ('delete', old.rowid, old.Subject, old.FromName, old.FromAddress, old.BodyText);
        END;
        """,
        """
        CREATE TRIGGER IF NOT EXISTS Messages_au AFTER UPDATE ON Messages BEGIN
            INSERT INTO MessageSearch(MessageSearch, rowid, Subject, FromName, FromAddress, BodyText)
            VALUES ('delete', old.rowid, old.Subject, old.FromName, old.FromAddress, old.BodyText);
            INSERT INTO MessageSearch(rowid, Subject, FromName, FromAddress, BodyText)
            VALUES (new.rowid, new.Subject, new.FromName, new.FromAddress, new.BodyText);
        END;
        """,
    ];

    /// <summary>
    /// SQL creating the remote image rules table in a global store that predates it.
    /// </summary>
    private const string RemoteImageRulesTable =
        "CREATE TABLE IF NOT EXISTS RemoteImageRules (Rule TEXT NOT NULL PRIMARY KEY, AddedAt INTEGER NOT NULL);";

    /// <summary>
    /// Ensures the global store (accounts, labels, label links) exists.
    /// </summary>
    /// <param name="context">The global database context.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the schema is ready.</returns>
    public static async Task InitializeGlobalAsync(GlobalDbContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // WAL lets the UI read while the background worker writes; it is a no-op for in-memory databases.
        await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
        await context.Database.EnsureCreatedAsync(cancellationToken);

        // EnsureCreated leaves an existing database untouched, so tables added later are created here (idempotent).
        await context.Database.ExecuteSqlRawAsync(RemoteImageRulesTable, cancellationToken);
    }

    /// <summary>
    /// Ensures the schema and full-text index of one account's database exist.
    /// </summary>
    /// <param name="context">The account database context.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the schema is ready.</returns>
    public static async Task InitializeAccountAsync(AccountDbContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        await context.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
        await context.Database.EnsureCreatedAsync(cancellationToken);
        await EnsureFlagSyncColumnAsync(context, cancellationToken);
        await EnsureDeletionColumnsAsync(context, cancellationToken);

        // Same name EF gives the index, so a new database (created by EnsureCreated above) is unaffected.
        await context.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_Messages_MessageIdHeader ON Messages (MessageIdHeader);",
            cancellationToken);
        foreach (var statement in FullTextStatements)
        {
            await context.Database.ExecuteSqlRawAsync(statement, cancellationToken);
        }
    }

    /// <summary>
    /// Adds the <c>IsFlagSyncPending</c> column to a Messages table created by an earlier version; does nothing when it exists.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the column exists.</returns>
    internal static async Task EnsureFlagSyncColumnAsync(AccountDbContext context, CancellationToken cancellationToken = default)
    {
        await EnsureColumnAsync(
            context,
            "SELECT COUNT(*) AS Value FROM pragma_table_info('Messages') WHERE name = 'IsFlagSyncPending'",
            "ALTER TABLE Messages ADD COLUMN IsFlagSyncPending INTEGER NOT NULL DEFAULT 0;",
            cancellationToken);
    }

    /// <summary>
    /// Adds the columns behind background deletion to a database created by an earlier version; does nothing when they exist.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the columns exist.</returns>
    internal static async Task EnsureDeletionColumnsAsync(AccountDbContext context, CancellationToken cancellationToken = default)
    {
        await EnsureColumnAsync(
            context,
            "SELECT COUNT(*) AS Value FROM pragma_table_info('Messages') WHERE name = 'IsDeletePending'",
            "ALTER TABLE Messages ADD COLUMN IsDeletePending INTEGER NOT NULL DEFAULT 0;",
            cancellationToken);
        await EnsureColumnAsync(
            context,
            "SELECT COUNT(*) AS Value FROM pragma_table_info('Folders') WHERE name = 'EmptyThroughUid'",
            "ALTER TABLE Folders ADD COLUMN EmptyThroughUid INTEGER NULL;",
            cancellationToken);

        // Only the few rows waiting for deletion are indexed, so looking for work stays cheap on large mailboxes.
        await context.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_Messages_DeletePending ON Messages (FolderId, Uid) WHERE IsDeletePending = 1;",
            cancellationToken);
    }

    /// <summary>
    /// Runs a statement that adds a column unless the column already exists.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="countStatement">The query returning how many columns of that name exist.</param>
    /// <param name="alterStatement">The statement adding the column.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the column exists.</returns>
    private static async Task EnsureColumnAsync(
        AccountDbContext context,
        string countStatement,
        string alterStatement,
        CancellationToken cancellationToken)
    {
        var existing = await context.Database.SqlQueryRaw<int>(countStatement).SingleAsync(cancellationToken);
        if (existing > 0)
        {
            return;
        }

        await context.Database.ExecuteSqlRawAsync(alterStatement, cancellationToken);
    }
}
