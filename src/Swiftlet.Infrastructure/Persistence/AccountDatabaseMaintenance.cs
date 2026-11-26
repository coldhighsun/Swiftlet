using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SQLitePCL;

namespace Swiftlet.Infrastructure.Persistence;

/// <summary>
/// The size of a database file before and after compacting it.
/// </summary>
/// <param name="BytesBefore">The size in bytes before.</param>
/// <param name="BytesAfter">The size in bytes afterwards.</param>
/// <param name="IndexRepaired">Whether compacting broke the full-text index mapping, so it was rebuilt once more.</param>
internal sealed record CompactionResult(long BytesBefore, long BytesAfter, bool IndexRepaired);

/// <summary>
/// Keeps an account database small: compresses HTML bodies left over from earlier versions and compacts the file.
/// The caller must hold the account's lock so that no sync or deletion writes to the database meanwhile.
/// </summary>
internal sealed class AccountDatabaseMaintenance
{
    /// <summary>
    /// How long a statement waits for another writer. Kept short because this wait cannot be cancelled and the work can
    /// simply be done later.
    /// </summary>
    private const int BusyTimeoutSeconds = 5;

    /// <summary>
    /// The SQLite result code of a statement stopped by <c>sqlite3_interrupt</c>.
    /// </summary>
    private const int SqliteInterrupted = 9;

    /// <summary>
    /// The SQLite result code reported when the full-text index does not match its content table.
    /// </summary>
    private const int SqliteCorrupt = 11;

    /// <summary>
    /// Gets the share of free pages above which a database is compacted.
    /// </summary>
    public double MinFreeRatio { get; init; } = 0.25;

    /// <summary>
    /// Gets the amount of free space above which a database is compacted; both limits must be exceeded.
    /// </summary>
    public long MinFreeBytes { get; init; } = 10 * 1024 * 1024;

    /// <summary>
    /// Gets the number of HTML bodies compressed per transaction.
    /// </summary>
    public int HtmlBatchSize { get; init; } = 100;

    /// <summary>
    /// Compresses the next batch of HTML bodies still stored as plain text, if that task is pending.
    /// </summary>
    /// <param name="context">The account's context.</param>
    /// <param name="afterRowId">The last row handled by the previous batch; 0 to start.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The last row handled, to pass to the next call, or <see langword="null"/> when nothing is left.</returns>
    public async Task<long?> CompressHtmlBatchAsync(AccountDbContext context, long afterRowId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return await WithConnectionAsync<long?>(
            context,
            connection =>
            {
                var tasks = ReadTasks(connection);
                if (!tasks.HasFlag(AccountDatabaseTasks.CompressHtml))
                {
                    return null;
                }

                using var transaction = connection.BeginTransaction();
                var rows = new List<(long RowId, string Html)>();
                using (var select = CreateCommand(
                    connection,
                    "SELECT rowid, BodyHtml FROM Messages WHERE rowid > $after AND typeof(BodyHtml) = 'text' ORDER BY rowid LIMIT $take"))
                {
                    select.Transaction = transaction;
                    select.Parameters.AddWithValue("$after", afterRowId);
                    select.Parameters.AddWithValue("$take", HtmlBatchSize);
                    using var reader = select.ExecuteReader();
                    while (reader.Read())
                    {
                        rows.Add((reader.GetInt64(0), reader.GetString(1)));
                    }
                }

                foreach (var (rowId, html) in rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var update = CreateCommand(connection, "UPDATE Messages SET BodyHtml = $html WHERE rowid = $rowId");
                    update.Transaction = transaction;
                    update.Parameters.AddWithValue("$html", HtmlBodyCodec.Encode(html));
                    update.Parameters.AddWithValue("$rowId", rowId);
                    update.ExecuteNonQuery();
                }

                if (rows.Count == 0)
                {
                    WriteTasks(connection, tasks & ~AccountDatabaseTasks.CompressHtml, transaction);
                }

                transaction.Commit();
                return rows.Count == 0 ? null : rows[^1].RowId;
            },
            cancellationToken);
    }

    /// <summary>
    /// Rebuilds the full-text index and compacts the file when that is pending or enough of the file is free.
    /// Cancelling interrupts the running statement; the database stays intact and the work is repeated later.
    /// </summary>
    /// <param name="context">The account's context.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The sizes, or <see langword="null"/> when there was no reason to compact.</returns>
    public async Task<CompactionResult?> CompactIfNeededAsync(AccountDbContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return await WithConnectionAsync(
            context,
            connection =>
            {
                var tasks = ReadTasks(connection);
                var pageSize = ReadNumber(connection, "PRAGMA page_size");
                var pages = ReadNumber(connection, "PRAGMA page_count");
                var free = ReadNumber(connection, "PRAGMA freelist_count");
                if (!tasks.HasFlag(AccountDatabaseTasks.Compact)
                    && (free * pageSize <= MinFreeBytes || free <= pages * MinFreeRatio))
                {
                    return null;
                }

                // Recorded first, so a run cut short anywhere below is repeated, including the index check.
                WriteTasks(connection, tasks | AccountDatabaseTasks.Compact);

                // Rebuilding first also shrinks an index bloated by many small writes; VACUUM then returns the space.
                Run(connection, "INSERT INTO MessageSearch(MessageSearch) VALUES('rebuild')", cancellationToken);
                Run(connection, "VACUUM", cancellationToken);
                var repaired = VerifySearchIndex(connection, cancellationToken);
                WriteTasks(connection, tasks & ~AccountDatabaseTasks.Compact);
                var after = ReadNumber(connection, "PRAGMA page_count");

                // In WAL mode the main file only shrinks at a checkpoint. A busy database reports that in a result row
                // instead of failing, and a later checkpoint finishes the job.
                Run(connection, "PRAGMA wal_checkpoint(TRUNCATE)", cancellationToken);
                return new CompactionResult(pages * pageSize, after * pageSize, repaired);
            },
            cancellationToken);
    }

    /// <summary>
    /// Checks the full-text index against the messages and rebuilds it when they disagree. VACUUM may renumber the rows
    /// of a table without an integer primary key, and the external-content index refers to messages by row number.
    /// </summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> when the index had to be rebuilt.</returns>
    internal static bool VerifySearchIndex(SqliteConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            // A rank of 1 makes the check compare the index with the content table, not only with itself.
            Run(connection, "INSERT INTO MessageSearch(MessageSearch, rank) VALUES('integrity-check', 1)", cancellationToken);
            return false;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteCorrupt)
        {
            // The expected answer for an index that no longer matches; rebuilding from the messages repairs it.
            Run(connection, "INSERT INTO MessageSearch(MessageSearch) VALUES('rebuild')", cancellationToken);
            return true;
        }
    }

    /// <summary>
    /// Runs work on the context's open connection, interrupting the running statement when cancelled.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="context">The account's context.</param>
    /// <param name="work">The work.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result of the work.</returns>
    private static async Task<T> WithConnectionAsync<T>(
        AccountDbContext context,
        Func<SqliteConnection, T> work,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await context.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            var connection = (SqliteConnection)context.Database.GetDbConnection();
            using var interrupt = cancellationToken.Register(() => raw.sqlite3_interrupt(connection.Handle));
            return work(connection);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == SqliteInterrupted && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The database maintenance was interrupted.", ex, cancellationToken);
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// Creates a command with the maintenance busy timeout.
    /// </summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="sql">The statement, built only from constants and numbers.</param>
    /// <returns>The command.</returns>
    private static SqliteCommand CreateCommand(SqliteConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = BusyTimeoutSeconds;
        return command;
    }

    /// <summary>
    /// Runs a statement unless cancellation was already requested.
    /// </summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="sql">The statement.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    private static void Run(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var command = CreateCommand(connection, sql);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Reads a single number.
    /// </summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="sql">The query.</param>
    /// <returns>The number.</returns>
    private static long ReadNumber(SqliteConnection connection, string sql)
    {
        using var command = CreateCommand(connection, sql);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Reads the pending tasks of a database.
    /// </summary>
    /// <param name="connection">The open connection.</param>
    /// <returns>The pending tasks.</returns>
    private static AccountDatabaseTasks ReadTasks(SqliteConnection connection)
    {
        return (AccountDatabaseTasks)ReadNumber(connection, "PRAGMA user_version");
    }

    /// <summary>
    /// Stores the pending tasks of a database.
    /// </summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="tasks">The pending tasks.</param>
    /// <param name="transaction">The surrounding transaction, if any.</param>
    private static void WriteTasks(SqliteConnection connection, AccountDatabaseTasks tasks, SqliteTransaction? transaction = null)
    {
        // PRAGMA statements take no parameters.
        using var command = CreateCommand(
            connection,
            string.Create(CultureInfo.InvariantCulture, $"PRAGMA user_version = {(int)tasks};"));
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }
}
