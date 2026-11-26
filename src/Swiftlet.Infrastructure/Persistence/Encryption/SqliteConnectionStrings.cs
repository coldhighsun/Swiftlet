using Microsoft.Data.Sqlite;

namespace Swiftlet.Infrastructure.Persistence.Encryption;

/// <summary>
/// Builds the connection strings of the application's databases, encrypted or not.
/// </summary>
public static class SqliteConnectionStrings
{
    /// <summary>
    /// How long a statement waits for a locked database, in seconds.
    /// </summary>
    private const int TimeoutSeconds = 30;

    /// <summary>
    /// Builds a connection string. A key is used as it is (raw), so opening does not run a slow key derivation.
    /// </summary>
    /// <param name="databasePath">The database file.</param>
    /// <param name="keyHex">The 64 hexadecimal characters of the key, or <see langword="null"/> for a plain database.</param>
    /// <param name="pooling">Whether connections are pooled.</param>
    /// <returns>The connection string; it contains the key and must not be logged.</returns>
    public static string Build(string databasePath, string? keyHex, bool pooling = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            DefaultTimeout = TimeoutSeconds,
            Pooling = pooling,
        };
        if (keyHex is not null)
        {
            builder.Password = $"raw:{keyHex}";
        }

        return builder.ToString();
    }
}
