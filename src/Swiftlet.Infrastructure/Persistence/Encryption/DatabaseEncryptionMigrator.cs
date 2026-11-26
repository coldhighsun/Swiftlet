using Microsoft.Data.Sqlite;

namespace Swiftlet.Infrastructure.Persistence.Encryption;

/// <summary>
/// Rewrites a plain database encrypted. An interrupted conversion is rolled back by SQLite from its journal, so the file is
/// always entirely plain or entirely encrypted.
/// </summary>
/// <param name="availableSpace">Gets the free bytes at a database path; the disk of the file when omitted.</param>
public sealed class DatabaseEncryptionMigrator(Func<string, long>? availableSpace = null) : IDatabaseEncryptionMigrator
{
    /// <summary>
    /// The free space needed, as a multiple of the database size: the journal and the grown pages.
    /// </summary>
    private const int SpaceFactor = 2;

    /// <summary>
    /// How long a statement waits for a database another process holds, in seconds.
    /// </summary>
    private const int BusyTimeoutSeconds = 5;

    /// <summary>
    /// The SQLite result code of a database locked by another connection.
    /// </summary>
    private const int SqliteBusy = 5;

    /// <summary>
    /// The SQLite result code of a locked table.
    /// </summary>
    private const int SqliteLocked = 6;

    /// <summary>
    /// The SQLite result code of a full disk.
    /// </summary>
    private const int SqliteFull = 13;

    /// <inheritdoc />
    public async Task EncryptAsync(string path, string keyHex, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyHex);
        if (!keyHex.All(char.IsAsciiHexDigit))
        {
            // PRAGMA statements take no parameters, so nothing but hexadecimal digits may reach the statement.
            throw new ArgumentException("The key is not hexadecimal.", nameof(keyHex));
        }

        var required = SpaceFactor * (Length(path) + Length($"{path}-wal"));
        if ((availableSpace ?? FreeSpace)(path) < required)
        {
            throw new DatabaseConversionException(DatabaseConversionFailure.NotEnoughSpace, $"Encrypting needs {required} free bytes.");
        }

        try
        {
            await using (var plain = await OpenAsync(path, null, cancellationToken))
            {
                // Rekeying is only documented to work outside WAL mode, and leaving it needs exclusive access.
                var mode = await ScalarAsync(plain, "PRAGMA journal_mode=DELETE", cancellationToken);
                if (!string.Equals(mode, "delete", StringComparison.OrdinalIgnoreCase))
                {
                    throw new DatabaseConversionException(DatabaseConversionFailure.InUse, "The database is in use.");
                }

                await ScalarAsync(plain, $"PRAGMA rekey = 'raw:{keyHex}'", cancellationToken);
            }

            await using var encrypted = await OpenAsync(path, keyHex, cancellationToken);
            var check = await ScalarAsync(encrypted, "PRAGMA quick_check", cancellationToken);
            if (!string.Equals(check, "ok", StringComparison.Ordinal))
            {
                throw new DatabaseConversionException(DatabaseConversionFailure.Failed, $"The converted database failed its check: {check}");
            }

            await ScalarAsync(encrypted, "PRAGMA journal_mode=WAL", cancellationToken);
        }
        catch (SqliteException ex)
        {
            var failure = ex.SqliteErrorCode switch
            {
                SqliteBusy or SqliteLocked => DatabaseConversionFailure.InUse,
                SqliteFull => DatabaseConversionFailure.NotEnoughSpace,
                _ => DatabaseConversionFailure.Failed,
            };
            throw new DatabaseConversionException(failure, ex.Message, ex);
        }
    }

    /// <summary>
    /// Gets the size of a file.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>The size, or 0 when it does not exist.</returns>
    private static long Length(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? file.Length : 0;
    }

    /// <summary>
    /// Gets the free space of the disk holding a file.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <returns>The free bytes, or the largest value when the system cannot tell.</returns>
    private static long FreeSpace(string path)
    {
        var full = Path.GetFullPath(path);
        try
        {
            // Windows wants the drive; elsewhere the directory finds the file system it is mounted on.
            var location = OperatingSystem.IsWindows() ? Path.GetPathRoot(full)! : Path.GetDirectoryName(full)!;
            return new DriveInfo(location).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // A network share has no drive to ask. A really full disk still stops the conversion safely.
            return long.MaxValue;
        }
    }

    /// <summary>
    /// Opens an unpooled connection, so closing it releases the file at once.
    /// </summary>
    /// <param name="path">The database file.</param>
    /// <param name="keyHex">The key, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The open connection.</returns>
    private static async Task<SqliteConnection> OpenAsync(string path, string? keyHex, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(SqliteConnectionStrings.Build(path, keyHex, pooling: false));
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            // The caller never receives the connection, so it is released before the failure propagates.
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Runs a statement.
    /// </summary>
    /// <param name="connection">The connection.</param>
    /// <param name="sql">The statement.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The first value of the result as text, if any.</returns>
    private static async Task<string?> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = BusyTimeoutSeconds;
        command.CommandText = sql;
        return (await command.ExecuteScalarAsync(cancellationToken))?.ToString();
    }
}
