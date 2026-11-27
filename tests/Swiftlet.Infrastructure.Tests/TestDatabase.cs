using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Labels;
using Swiftlet.Domain.Messages;
using Swiftlet.Infrastructure.Persistence;
using Swiftlet.Infrastructure.Persistence.Repositories;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// An <see cref="IAccountDbContextFactory"/> that keeps one open in-memory SQLite database per account.
/// </summary>
public sealed class InMemoryAccountDbContextFactory : IAccountDbContextFactory, IAsyncDisposable
{
    /// <summary>
    /// The open connections that keep the in-memory databases alive, by account.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, SqliteConnection> _connections = new();

    /// <summary>
    /// The connections of deleted databases; like an unlinked file they stay usable for work already in flight.
    /// </summary>
    private readonly List<SqliteConnection> _detached = [];

    /// <summary>
    /// Serializes creation and deletion of databases.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Gets the identifiers of the accounts whose database was deleted through <see cref="DeleteAsync"/>.
    /// </summary>
    public List<Guid> DeletedAccounts { get; } = [];

    /// <summary>
    /// Gets the identifiers of the accounts that currently have a database.
    /// </summary>
    public IReadOnlyCollection<Guid> OpenAccounts => [.. _connections.Keys];

    /// <inheritdoc />
    public async Task<AccountDbContext> CreateAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var isNew = false;
            if (!_connections.TryGetValue(accountId, out var connection))
            {
                connection = new SqliteConnection("DataSource=:memory:");
                await connection.OpenAsync(cancellationToken);
                _connections[accountId] = connection;
                isNew = true;
            }

            var context = new AccountDbContext(new DbContextOptionsBuilder<AccountDbContext>().UseSqlite(connection).Options);
            if (isNew)
            {
                await SqliteSchemaInitializer.InitializeAccountAsync(context, cancellationToken);
            }

            return context;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            DeletedAccounts.Add(accountId);
            if (_connections.TryRemove(accountId, out var connection))
            {
                _detached.Add(connection);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var connection in _connections.Values)
        {
            await connection.DisposeAsync();
        }

        foreach (var connection in _detached)
        {
            await connection.DisposeAsync();
        }

        _connections.Clear();
        _gate.Dispose();
    }
}

/// <summary>
/// A second view over a <see cref="TestDatabase"/> with its own contexts, used to verify what was really persisted.
/// </summary>
public sealed class DatabaseVerifier : IAsyncDisposable
{
    /// <summary>
    /// The factory of the account databases.
    /// </summary>
    private readonly IAccountDbContextFactory _factory;

    /// <summary>
    /// The scope-like provider of account contexts owned by this verifier.
    /// </summary>
    private readonly AccountContextProvider _provider;

    /// <summary>
    /// Initializes a new instance of the <see cref="DatabaseVerifier"/> class.
    /// </summary>
    /// <param name="global">A fresh global context.</param>
    /// <param name="factory">The factory of the account databases.</param>
    internal DatabaseVerifier(GlobalDbContext global, IAccountDbContextFactory factory)
    {
        Global = global;
        _factory = factory;
        _provider = new AccountContextProvider(factory);
    }

    /// <summary>
    /// Gets the verifier's own global context.
    /// </summary>
    public GlobalDbContext Global { get; }

    /// <summary>
    /// Creates an account repository on the verifier's contexts.
    /// </summary>
    /// <returns>The repository.</returns>
    internal AccountRepository Accounts()
    {
        return new AccountRepository(Global, _factory);
    }

    /// <summary>
    /// Creates a folder repository on the verifier's contexts.
    /// </summary>
    /// <returns>The repository.</returns>
    internal FolderRepository Folders()
    {
        return new FolderRepository(_provider);
    }

    /// <summary>
    /// Creates a message repository on the verifier's contexts.
    /// </summary>
    /// <returns>The repository.</returns>
    internal MessageRepository Messages()
    {
        return new MessageRepository(Global, _provider);
    }

    /// <summary>
    /// Creates a label repository on the verifier's contexts.
    /// </summary>
    /// <returns>The repository.</returns>
    internal LabelRepository Labels()
    {
        return new LabelRepository(Global);
    }

    /// <summary>
    /// Gets the verifier's context for one account's database.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The context, disposed with the verifier.</returns>
    public async Task<AccountDbContext> AccountAsync(Guid accountId, CancellationToken cancellationToken)
    {
        return await _provider.GetAsync(accountId, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await Global.DisposeAsync();
    }
}

/// <summary>
/// An isolated in-memory global SQLite database plus one in-memory database per account, with the Swiftlet schema.
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    /// <summary>
    /// The open connection that keeps the in-memory global database alive.
    /// </summary>
    private readonly SqliteConnection _connection;

    /// <summary>
    /// The context options shared by all global contexts of this database.
    /// </summary>
    private readonly DbContextOptions<GlobalDbContext> _options;

    /// <summary>
    /// The account context provider shared by the repositories created from this instance.
    /// </summary>
    private readonly AccountContextProvider _provider;

    /// <summary>
    /// Gets the global context used by the test (accounts, labels, label links).
    /// </summary>
    public GlobalDbContext Context { get; }

    /// <summary>
    /// Gets the factory of the per-account databases.
    /// </summary>
    public InMemoryAccountDbContextFactory Factory { get; } = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="TestDatabase"/> class.
    /// </summary>
    /// <param name="connection">The open SQLite connection of the global database.</param>
    private TestDatabase(SqliteConnection connection)
    {
        _connection = connection;
        _options = new DbContextOptionsBuilder<GlobalDbContext>().UseSqlite(connection).Options;
        Context = new GlobalDbContext(_options);
        _provider = new AccountContextProvider(Factory);
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
        await SqliteSchemaInitializer.InitializeGlobalAsync(database.Context, cancellationToken);
        return database;
    }

    /// <summary>
    /// Creates an account repository on the shared contexts.
    /// </summary>
    /// <returns>The repository.</returns>
    internal AccountRepository CreateAccountRepository()
    {
        return new AccountRepository(Context, Factory);
    }

    /// <summary>
    /// Creates a folder repository on the shared contexts.
    /// </summary>
    /// <returns>The repository.</returns>
    internal FolderRepository CreateFolderRepository()
    {
        return new FolderRepository(_provider);
    }

    /// <summary>
    /// Creates a message repository on the shared contexts.
    /// </summary>
    /// <returns>The repository.</returns>
    internal MessageRepository CreateMessageRepository()
    {
        return new MessageRepository(Context, _provider);
    }

    /// <summary>
    /// Creates a label repository on the shared global context.
    /// </summary>
    /// <returns>The repository.</returns>
    internal LabelRepository CreateLabelRepository()
    {
        return new LabelRepository(Context);
    }

    /// <summary>
    /// Creates a contact repository on the shared global context.
    /// </summary>
    /// <returns>The repository.</returns>
    internal ContactRepository CreateContactRepository()
    {
        return new ContactRepository(Context);
    }

    /// <summary>
    /// Gets the shared context of one account's database (the one the repositories of this instance track).
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The context, disposed with this database.</returns>
    public async Task<AccountDbContext> AccountContextAsync(Guid accountId, CancellationToken cancellationToken)
    {
        return await _provider.GetAsync(accountId, cancellationToken);
    }

    /// <summary>
    /// Creates a second view with fresh contexts over the same databases, to verify data was actually persisted.
    /// </summary>
    /// <returns>A new verifier that the caller must dispose.</returns>
    public DatabaseVerifier CreateVerifier()
    {
        return new DatabaseVerifier(new GlobalDbContext(_options), Factory);
    }

    /// <summary>
    /// Inserts an account into the global store and a folder into the account's own database.
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
        await Context.SaveChangesAsync(cancellationToken);
        await CreateFolderRepository().AddAsync(folder, cancellationToken);
        return (account, folder);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await Context.DisposeAsync();
        await Factory.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
