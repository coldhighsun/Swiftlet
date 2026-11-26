using Microsoft.Data.Sqlite;
using Swiftlet.Infrastructure.Persistence.Encryption;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// A temporary directory with helpers for the tests that need real, possibly encrypted, database files.
/// </summary>
internal sealed class EncryptionTestDirectory : IDisposable
{
    /// <summary>
    /// The key used by tests.
    /// </summary>
    public const string Key = "00112233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF";

    /// <summary>
    /// A second key that never fits databases created with <see cref="Key"/>.
    /// </summary>
    public const string OtherKey = "FFEEDDCCBBAA99887766554433221100FFEEDDCCBBAA99887766554433221100";

    /// <summary>
    /// A text stored in every created database, to search for in the raw files.
    /// </summary>
    public const string Marker = "SECRETMARKER";

    /// <summary>
    /// Gets the directory path.
    /// </summary>
    public string Root { get; } = Directory.CreateTempSubdirectory("swiftlet-crypt-test-").FullName;

    /// <summary>
    /// Gets the path of a file in the directory.
    /// </summary>
    /// <param name="name">The relative name.</param>
    /// <returns>The full path.</returns>
    public string File(string name)
    {
        return Path.Combine(Root, name);
    }

    /// <summary>
    /// Creates a database with one table holding marker rows.
    /// </summary>
    /// <param name="path">The database path.</param>
    /// <param name="key">The key, or null for a plain database.</param>
    /// <param name="rows">The number of rows.</param>
    public static void CreateDatabase(string path, string? key, int rows = 1)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Scalar(path, key, "CREATE TABLE Items(Value TEXT)");
        Scalar(
            path,
            key,
            $"WITH RECURSIVE r(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM r WHERE x < {rows}) INSERT INTO Items SELECT '{Marker}' || x FROM r");
    }

    /// <summary>
    /// Runs one statement on a new unpooled connection.
    /// </summary>
    /// <param name="path">The database path.</param>
    /// <param name="key">The key, or null.</param>
    /// <param name="sql">The statement.</param>
    /// <returns>The first value of the result, if any.</returns>
    public static object? Scalar(string path, string? key, string sql)
    {
        using var connection = new SqliteConnection(SqliteConnectionStrings.Build(path, key, pooling: false));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        // Only this test class's pools would be enough, but pooled handles of any context keep files locked on Windows.
        SqliteConnection.ClearAllPools();
        Directory.Delete(Root, recursive: true);
    }
}
