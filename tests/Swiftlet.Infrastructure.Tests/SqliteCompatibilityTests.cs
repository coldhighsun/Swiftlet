using System.Text;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Swiftlet.Infrastructure.Persistence.Encryption;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests that the bundled SQLite build still opens databases encrypted by an earlier version of it.
/// </summary>
/// <remarks>
/// The sample database was written once by SQLite3 Multiple Ciphers 2.7.1 with <see cref="EncryptionTestDirectory.Key"/>
/// and the default cipher. Never regenerate it to make a failing test pass: a failure means users of an
/// earlier release could no longer open their mail.
/// </remarks>
public sealed class SqliteCompatibilityTests : IDisposable
{
    /// <summary>
    /// The number of rows in the sample database; they span several pages.
    /// </summary>
    private const int RowCount = 500;

    /// <summary>
    /// The temporary directory of one test.
    /// </summary>
    private readonly EncryptionTestDirectory _directory = new();

    /// <summary>
    /// A private copy of the sample database, because opening it writes to it.
    /// </summary>
    private readonly string _path;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteCompatibilityTests"/> class.
    /// </summary>
    public SqliteCompatibilityTests()
    {
        _path = _directory.File("old.db");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "TestData", "encrypted-sqlite3mc-2.7.1.db"), _path);
    }

    /// <summary>
    /// Verifies the sample is really encrypted, so the other tests exercise decryption.
    /// </summary>
    [Fact]
    public void Sample_WithoutKey_CannotBeRead()
    {
        var content = Encoding.Latin1.GetString(File.ReadAllBytes(_path));
        var act = () => EncryptionTestDirectory.Scalar(_path, null, "SELECT COUNT(*) FROM Items");

        content.Should().NotContain(EncryptionTestDirectory.Marker).And.NotContain("SQLite format 3");
        act.Should().Throw<SqliteException>().Which.SqliteErrorCode.Should().Be(26);
    }

    /// <summary>
    /// Verifies every value written by the earlier version is read back unchanged.
    /// </summary>
    [Fact]
    public void Open_DatabaseOfEarlierVersion_ReadsEveryRowUnchanged()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Id, Value, Data FROM Items ORDER BY Id";

        var rows = new List<(long Id, string Value, string Data)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add((reader.GetInt64(0), reader.GetString(1), Encoding.UTF8.GetString(reader.GetFieldValue<byte[]>(2))));
            }
        }

        rows.Should().Equal(Enumerable.Range(1, RowCount).Select(i =>
            ((long)i, $"{EncryptionTestDirectory.Marker} {i} 加密邮件", new string('a', 100) + i)));
        Scalar(connection, "PRAGMA integrity_check").Should().Be("ok");
    }

    /// <summary>
    /// Verifies a full-text index built by the earlier version still matches its content and finds rows.
    /// </summary>
    [Fact]
    public void Search_IndexOfEarlierVersion_FindsRows()
    {
        using var connection = Open();

        // Throws when the index and the content table disagree.
        Scalar(connection, "INSERT INTO ItemsFts(ItemsFts, rank) VALUES('integrity-check', 1)");

        Scalar(connection, "SELECT COUNT(*) FROM ItemsFts WHERE ItemsFts MATCH '\"加密邮件\"'").Should().Be((long)RowCount);
    }

    /// <summary>
    /// Verifies a database of the earlier version can be written to and reopened.
    /// </summary>
    [Fact]
    public void Write_DatabaseOfEarlierVersion_PersistsAfterReopening()
    {
        using (var connection = Open())
        {
            Scalar(connection, "UPDATE Items SET Value = 'changed' WHERE Id = 1");
        }

        using var reopened = Open();

        Scalar(reopened, "SELECT Value FROM Items WHERE Id = 1").Should().Be("changed");
        Scalar(reopened, "PRAGMA integrity_check").Should().Be("ok");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _directory.Dispose();
    }

    /// <summary>
    /// Runs one statement.
    /// </summary>
    /// <param name="connection">The open connection.</param>
    /// <param name="sql">The statement.</param>
    /// <returns>The first value of the result, if any.</returns>
    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    /// <summary>
    /// Opens the copy the way the application opens an encrypted database.
    /// </summary>
    /// <returns>The open, unpooled connection.</returns>
    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(SqliteConnectionStrings.Build(_path, EncryptionTestDirectory.Key, pooling: false));
        connection.Open();
        return connection;
    }
}
