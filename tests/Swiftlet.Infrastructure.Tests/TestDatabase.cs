using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;
using Swiftlet.Infrastructure.Persistence;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// An isolated in-memory SQLite database with the Swiftlet schema and FTS5 index.
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    /// <summary>
    /// The open connection that keeps the in-memory database alive.
    /// </summary>
    private readonly SqliteConnection _connection;

    /// <summary>
    /// The context options shared by all contexts of this database.
    /// </summary>
    private readonly DbContextOptions<SwiftletDbContext> _options;

    /// <summary>
    /// Gets the context used by the test.
    /// </summary>
    public SwiftletDbContext Context { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="TestDatabase"/> class.
    /// </summary>
    /// <param name="connection">The open SQLite connection.</param>
    private TestDatabase(SqliteConnection connection)
    {
        _connection = connection;
        _options = new DbContextOptionsBuilder<SwiftletDbContext>().UseSqlite(connection).Options;
        Context = new SwiftletDbContext(_options);
    }

    /// <summary>
    /// Creates and initializes a new database.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The ready database.</returns>
    public static async Task<TestDatabase> CreateAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync(cancellationToken);
        var database = new TestDatabase(connection);
        await SqliteSchemaInitializer.InitializeAsync(database.Context, cancellationToken);
        return database;
    }

    /// <summary>
    /// Creates a second context over the same database, to verify data was actually persisted.
    /// </summary>
    /// <returns>A new context.</returns>
    public SwiftletDbContext CreateContext()
    {
        return new SwiftletDbContext(_options);
    }

    /// <summary>
    /// Inserts an account and a folder to hang messages on.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The created account and folder.</returns>
    public async Task<(MailAccount Account, MailFolder Folder)> SeedAccountAsync(CancellationToken cancellationToken)
    {
        var account = MailAccount.Create(
            "Me",
            $"{Guid.NewGuid():N}@example.com",
            AuthMethod.Password,
            new ServerEndpoint("imap.example.com", 993, true),
            new ServerEndpoint("smtp.example.com", 465, true),
            DateTimeOffset.UtcNow);
        var folder = MailFolder.Create(account.Id, "INBOX", "Inbox", FolderRole.Inbox);
        Context.Accounts.Add(account);
        Context.Folders.Add(folder);
        await Context.SaveChangesAsync(cancellationToken);
        return (account, folder);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Context.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
