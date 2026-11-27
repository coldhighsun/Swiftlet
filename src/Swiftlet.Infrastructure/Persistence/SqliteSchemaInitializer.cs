using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Swiftlet.Domain.Accounts;

namespace Swiftlet.Infrastructure.Persistence;

/// <summary>
/// Creates the database schema and the FTS5 full-text index with its synchronization triggers.
/// </summary>
public static class SqliteSchemaInitializer
{
    /// <summary>
    /// The trigger re-indexing a message when an indexed column changes. Other updates (read and starred state, moves,
    /// pending marks) leave the index alone.
    /// </summary>
    private const string UpdateTrigger =
        """
        CREATE TRIGGER IF NOT EXISTS Messages_au AFTER UPDATE OF Subject, FromName, FromAddress, BodyText ON Messages BEGIN
            INSERT INTO MessageSearch(MessageSearch, rowid, Subject, FromName, FromAddress, BodyText)
            VALUES ('delete', old.rowid, old.Subject, old.FromName, old.FromAddress, old.BodyText);
            INSERT INTO MessageSearch(rowid, Subject, FromName, FromAddress, BodyText)
            VALUES (new.rowid, new.Subject, new.FromName, new.FromAddress, new.BodyText);
        END;
        """;

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
        UpdateTrigger,
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
        await EnsureCustomDisplayNameColumnAsync(context, cancellationToken);
    }

    /// <summary>
    /// Adds the <c>HasCustomDisplayName</c> column to an Accounts table created by an earlier version; does nothing when it
    /// exists. Those versions filled a missing name of a password account with the local part of the address, so any other
    /// name was typed by the user. Their OAuth sign-in never passed a typed name on.
    /// </summary>
    /// <param name="context">The global database context.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the column exists.</returns>
    private static async Task EnsureCustomDisplayNameColumnAsync(GlobalDbContext context, CancellationToken cancellationToken)
    {
        var existing = await context.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM pragma_table_info('Accounts') WHERE name = 'HasCustomDisplayName'")
            .SingleAsync(cancellationToken);
        if (existing > 0)
        {
            return;
        }

        // One transaction: the names are classified exactly when the column appears.
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.Database.ExecuteSqlRawAsync(
            "ALTER TABLE Accounts ADD COLUMN HasCustomDisplayName INTEGER NOT NULL DEFAULT 0;",
            cancellationToken);
        var password = (int)AuthMethod.Password;
        await context.Database.ExecuteSqlAsync(
            $"""
            UPDATE Accounts SET HasCustomDisplayName = 1
            WHERE AuthMethod = {password}
                AND DisplayName <> substr(EmailAddress, 1, instr(EmailAddress, '@') - 1);
            """,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
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
        await UpgradeUpdateTriggerAsync(context, cancellationToken);
        foreach (var statement in FullTextStatements)
        {
            await context.Database.ExecuteSqlRawAsync(statement, cancellationToken);
        }
    }

    /// <summary>
    /// Replaces the update trigger of an earlier version, which fired on every update. Such a database also stores its
    /// HTML bodies as plain text and carries an oversized index, so both maintenance tasks are recorded as pending.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when no outdated trigger is left.</returns>
    private static async Task UpgradeUpdateTriggerAsync(AccountDbContext context, CancellationToken cancellationToken)
    {
        var outdated = await context.Database
            .SqlQueryRaw<int>(
                "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'trigger' AND name = 'Messages_au' AND sql NOT LIKE '%AFTER UPDATE OF %'")
            .SingleAsync(cancellationToken);
        if (outdated == 0)
        {
            return;
        }

        // One transaction: the pending tasks are recorded exactly when the old trigger, their only evidence, disappears.
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.Database.ExecuteSqlRawAsync("DROP TRIGGER Messages_au;", cancellationToken);
        await context.Database.ExecuteSqlRawAsync(UpdateTrigger, cancellationToken);
        var pending = (int)(AccountDatabaseTasks.CompressHtml | AccountDatabaseTasks.Compact);
        await context.Database.ExecuteSqlRawAsync(
            string.Create(CultureInfo.InvariantCulture, $"PRAGMA user_version = {pending};"),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
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
    /// Adds the columns behind background deletion and moving to a database created by an earlier version; does nothing when
    /// they exist.
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
            "SELECT COUNT(*) AS Value FROM pragma_table_info('Messages') WHERE name = 'PendingMoveFolderId'",
            "ALTER TABLE Messages ADD COLUMN PendingMoveFolderId TEXT NULL;",
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

        // Covers the unread count of the folder tree, which would otherwise read every row and its message body.
        await context.Database.ExecuteSqlRawAsync(
            "CREATE INDEX IF NOT EXISTS IX_Messages_Unread ON Messages (FolderId, IsRead, IsDeletePending);",
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
