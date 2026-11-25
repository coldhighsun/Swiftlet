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
    /// Ensures the schema and full-text index exist.
    /// </summary>
    /// <param name="context">The database context.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the schema is ready.</returns>
    public static async Task InitializeAsync(SwiftletDbContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        await context.Database.EnsureCreatedAsync(cancellationToken);
        foreach (var statement in FullTextStatements)
        {
            await context.Database.ExecuteSqlRawAsync(statement, cancellationToken);
        }
    }
}
