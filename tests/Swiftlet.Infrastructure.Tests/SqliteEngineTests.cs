using System.Text;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Swiftlet.Infrastructure.Persistence.Encryption;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for the bundled SQLite build and <see cref="SqliteConnectionStrings"/>, against real files.
/// </summary>
public sealed class SqliteEngineTests : IDisposable
{
    /// <summary>
    /// The temporary directory of one test.
    /// </summary>
    private readonly EncryptionTestDirectory _directory = new();

    /// <summary>
    /// Verifies the loaded native library can encrypt and still has full-text search.
    /// </summary>
    [Fact]
    public void Engine_Loaded_IsMultipleCiphersWithFts5()
    {
        var path = _directory.File("engine.db");

        ((string)EncryptionTestDirectory.Scalar(path, null, "SELECT sqlite3mc_version()")!).Should().NotBeNullOrEmpty();
        ((long)EncryptionTestDirectory.Scalar(path, null, "SELECT sqlite_compileoption_used('ENABLE_FTS5')")!).Should().Be(1);
    }

    /// <summary>
    /// Verifies a database opened with a key leaks nothing and is closed to everyone without it.
    /// </summary>
    [Fact]
    public void Build_WithKey_CreatesFileWithoutPlainText()
    {
        var path = _directory.File("secret.db");

        EncryptionTestDirectory.CreateDatabase(path, EncryptionTestDirectory.Key);

        var content = Encoding.Latin1.GetString(File.ReadAllBytes(path));
        content.Should().NotContain(EncryptionTestDirectory.Marker).And.NotContain("SQLite format 3");
        var act = () => EncryptionTestDirectory.Scalar(path, null, "SELECT COUNT(*) FROM sqlite_master");
        act.Should().Throw<SqliteException>().Which.SqliteErrorCode.Should().Be(26);
    }

    /// <summary>
    /// Verifies no key means an ordinary SQLite file.
    /// </summary>
    [Fact]
    public void Build_WithoutKey_CreatesPlainFile()
    {
        var path = _directory.File("plain.db");

        EncryptionTestDirectory.CreateDatabase(path, null);

        Encoding.Latin1.GetString(File.ReadAllBytes(path), 0, 15).Should().Be("SQLite format 3");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _directory.Dispose();
    }
}
