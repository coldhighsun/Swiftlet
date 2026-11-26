using Microsoft.Data.Sqlite;

namespace Swiftlet.Infrastructure.Persistence.Encryption;

/// <summary>
/// What a database file turned out to be.
/// </summary>
public enum DatabaseFileState
{
    /// <summary>
    /// The file does not exist or is empty.
    /// </summary>
    Missing,

    /// <summary>
    /// An unencrypted database.
    /// </summary>
    Plain,

    /// <summary>
    /// A database the given key opens.
    /// </summary>
    Encrypted,

    /// <summary>
    /// Neither readable without a key nor with the given one.
    /// </summary>
    Unreadable,
}

/// <summary>
/// Determines the state of a database file by reading it; the file header alone is not trustworthy after an interrupted
/// conversion.
/// </summary>
public static class DatabaseProbe
{
    /// <summary>
    /// The SQLite result code of a damaged database.
    /// </summary>
    private const int SqliteCorrupt = 11;

    /// <summary>
    /// The SQLite result code of a file that is no database, which is also how a wrong or missing key shows.
    /// </summary>
    private const int SqliteNotADatabase = 26;

    /// <summary>
    /// How long a probe waits for a database another process holds, in seconds.
    /// </summary>
    private const int BusyTimeoutSeconds = 5;

    /// <summary>
    /// Probes a database file. Reading it applies a rollback journal left by an interrupted write.
    /// </summary>
    /// <param name="path">The database file.</param>
    /// <param name="keyHex">The key, or <see langword="null"/> when there is none.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The state; failures other than unreadable content (a locked file, a disk error) are thrown.</returns>
    public static async Task<DatabaseFileState> ProbeAsync(string path, string? keyHex, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var file = new FileInfo(path);
        if (!file.Exists || file.Length == 0)
        {
            return DatabaseFileState.Missing;
        }

        // A journal means a write was interrupted. A conversion journals the plain pages, so plain is tried first then.
        string?[] attempts = keyHex is null
            ? [null]
            : File.Exists($"{path}-journal") ? [null, keyHex] : [keyHex, null];
        foreach (var key in attempts)
        {
            if (await CanReadAsync(path, key, cancellationToken))
            {
                return key is null ? DatabaseFileState.Plain : DatabaseFileState.Encrypted;
            }
        }

        return DatabaseFileState.Unreadable;
    }

    /// <summary>
    /// Tries to read the schema.
    /// </summary>
    /// <param name="path">The database file.</param>
    /// <param name="keyHex">The key, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the content could be read this way.</returns>
    private static async Task<bool> CanReadAsync(string path, string? keyHex, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqliteConnection(SqliteConnectionStrings.Build(path, keyHex, pooling: false));
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = BusyTimeoutSeconds;
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master";
            await command.ExecuteScalarAsync(cancellationToken);
            return true;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is SqliteNotADatabase or SqliteCorrupt)
        {
            // This is the answer the probe is looking for, not a failure.
            return false;
        }
    }
}
