using System.Diagnostics;
using AwesomeAssertions;
using Microsoft.Data.Sqlite;
using Swiftlet.Infrastructure.Persistence.Encryption;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="DatabaseProbe"/> against real files.
/// </summary>
public sealed class DatabaseProbeTests : IDisposable
{
    /// <summary>
    /// The temporary directory of one test.
    /// </summary>
    private readonly EncryptionTestDirectory _directory = new();

    /// <summary>
    /// Probes a file with the test token of this test.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="key">The key, or null.</param>
    /// <returns>The state.</returns>
    private static Task<DatabaseFileState> ProbeAsync(string path, string? key)
    {
        return DatabaseProbe.ProbeAsync(path, key, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// Verifies a file that does not exist is missing and is not created.
    /// </summary>
    [Fact]
    public async Task ProbeAsync_NoFile_ReturnsMissing()
    {
        var path = _directory.File("none.db");

        (await ProbeAsync(path, EncryptionTestDirectory.Key)).Should().Be(DatabaseFileState.Missing);

        File.Exists(path).Should().BeFalse();
    }

    /// <summary>
    /// Verifies a zero-length file counts as missing.
    /// </summary>
    [Fact]
    public async Task ProbeAsync_EmptyFile_ReturnsMissing()
    {
        var path = _directory.File("empty.db");
        await File.WriteAllBytesAsync(path, [], TestContext.Current.CancellationToken);

        (await ProbeAsync(path, EncryptionTestDirectory.Key)).Should().Be(DatabaseFileState.Missing);
    }

    /// <summary>
    /// Verifies an ordinary database is plain, with or without a key at hand.
    /// </summary>
    /// <param name="key">The key given to the probe.</param>
    [Theory]
    [InlineData(null)]
    [InlineData(EncryptionTestDirectory.Key)]
    public async Task ProbeAsync_PlainFile_ReturnsPlain(string? key)
    {
        var path = _directory.File("plain.db");
        EncryptionTestDirectory.CreateDatabase(path, null);

        (await ProbeAsync(path, key)).Should().Be(DatabaseFileState.Plain);
    }

    /// <summary>
    /// Verifies the right key identifies an encrypted database.
    /// </summary>
    [Fact]
    public async Task ProbeAsync_EncryptedWithKey_ReturnsEncrypted()
    {
        var path = _directory.File("enc.db");
        EncryptionTestDirectory.CreateDatabase(path, EncryptionTestDirectory.Key);

        (await ProbeAsync(path, EncryptionTestDirectory.Key)).Should().Be(DatabaseFileState.Encrypted);
    }

    /// <summary>
    /// Verifies an encrypted database cannot be read without its key.
    /// </summary>
    /// <param name="key">The key given to the probe.</param>
    [Theory]
    [InlineData(null)]
    [InlineData(EncryptionTestDirectory.OtherKey)]
    public async Task ProbeAsync_EncryptedWithoutOrWrongKey_ReturnsUnreadable(string? key)
    {
        var path = _directory.File("enc.db");
        EncryptionTestDirectory.CreateDatabase(path, EncryptionTestDirectory.Key);

        (await ProbeAsync(path, key)).Should().Be(DatabaseFileState.Unreadable);
    }

    /// <summary>
    /// Verifies a file that is no database at all is unreadable and left alone.
    /// </summary>
    [Fact]
    public async Task ProbeAsync_Garbage_ReturnsUnreadable()
    {
        var path = _directory.File("garbage.db");
        var content = Enumerable.Range(0, 8192).Select(i => (byte)(i % 251)).ToArray();
        await File.WriteAllBytesAsync(path, content, TestContext.Current.CancellationToken);

        (await ProbeAsync(path, EncryptionTestDirectory.Key)).Should().Be(DatabaseFileState.Unreadable);

        (await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken)).Should().Equal(content);
    }

    /// <summary>
    /// Verifies the rollback journal left by an interrupted write is applied, restoring the committed data.
    /// </summary>
    [Fact]
    public async Task ProbeAsync_HotJournal_RecoversCommittedRows()
    {
        var source = _directory.File("source.db");
        var copy = _directory.File("copy.db");
        EncryptionTestDirectory.CreateDatabase(source, null, rows: 2000);
        await using (var connection = new SqliteConnection(SqliteConnectionStrings.Build(source, null, pooling: false)))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();

            // A tiny page cache forces the uncommitted pages into the file, as a crash in a large write would leave them.
            command.CommandText = "PRAGMA journal_mode=DELETE; PRAGMA cache_size=1; BEGIN; UPDATE Items SET Value = 'changed ' || Value;";
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            Copy(source, copy);
            Copy($"{source}-journal", $"{copy}-journal");
        }

        var state = await ProbeAsync(copy, EncryptionTestDirectory.Key);

        state.Should().Be(DatabaseFileState.Plain);
        File.Exists($"{copy}-journal").Should().BeFalse();
        EncryptionTestDirectory.Scalar(copy, null, "SELECT COUNT(*) FROM Items WHERE Value LIKE 'SECRETMARKER%'").Should().Be(2000L);
    }

    /// <summary>
    /// Copies a file another connection still has open and locked.
    /// </summary>
    /// <param name="from">The source.</param>
    /// <param name="to">The target.</param>
    private static void Copy(string from, string to)
    {
        // On macOS the advisory lock .NET takes on every opened file conflicts with the locks SQLite holds,
        // so the file is copied by a program that takes none.
        if (!OperatingSystem.IsWindows())
        {
            using var process = Process.Start("cp", [from, to]);
            process.WaitForExit();
            process.ExitCode.Should().Be(0);
            return;
        }

        using var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(to, FileMode.Create, FileAccess.Write);
        input.CopyTo(output);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _directory.Dispose();
    }
}
