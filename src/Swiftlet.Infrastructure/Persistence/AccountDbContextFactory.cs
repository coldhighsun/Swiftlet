using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Swiftlet.Infrastructure.Persistence;

/// <summary>
/// Stores each account's mail in its own SQLite file named after the account identifier inside one directory.
/// </summary>
/// <param name="directory">The directory holding the account databases.</param>
/// <param name="logger">The logger; optional.</param>
public sealed class AccountDbContextFactory(string directory, ILogger<AccountDbContextFactory>? logger = null) : IAccountDbContextFactory, IDisposable
{
    /// <summary>
    /// The accounts whose database schema has already been ensured in this process.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, bool> _initialized = new();

    /// <summary>
    /// Serializes schema creation and file deletion so two scopes cannot race on one database file.
    /// </summary>
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    /// <inheritdoc />
    public async Task<AccountDbContext> CreateAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var context = new AccountDbContext(BuildOptions(accountId));
        try
        {
            if (!_initialized.ContainsKey(accountId))
            {
                await _fileLock.WaitAsync(cancellationToken);
                try
                {
                    if (!_initialized.ContainsKey(accountId))
                    {
                        Directory.CreateDirectory(directory);
                        logger?.LogInformation("Opening the database of account {AccountId} for the first time in this run", accountId);
                        await SqliteSchemaInitializer.InitializeAccountAsync(context, cancellationToken);
                        _initialized[accountId] = true;
                    }
                }
                finally
                {
                    _fileLock.Release();
                }
            }

            return context;
        }
        catch
        {
            // The caller never receives the context, so it is released here before the failure propagates.
            await context.DisposeAsync();
            throw;
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            _initialized.TryRemove(accountId, out _);

            // Pooled connections keep the file open (and locked on Windows) after the contexts are gone.
            SqliteConnection.ClearAllPools();
            if (!Directory.Exists(directory))
            {
                // Nothing was ever created for any account, and File.Delete would fail on the missing directory.
                return;
            }

            var path = GetDatabasePath(accountId);
            logger?.LogInformation("Deleting the database files of account {AccountId}", accountId);
            foreach (var file in new[] { path, $"{path}-wal", $"{path}-shm" })
            {
                File.Delete(file);
            }
        }
        finally
        {
            _fileLock.Release();
        }
    }

    /// <summary>
    /// Gets the path of an account's database file.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <returns>The full file path.</returns>
    private string GetDatabasePath(Guid accountId)
    {
        return Path.Combine(directory, $"{accountId:D}.db");
    }

    /// <summary>
    /// Builds the context options for an account's database.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <returns>The options.</returns>
    private DbContextOptions<AccountDbContext> BuildOptions(Guid accountId)
    {
        return new DbContextOptionsBuilder<AccountDbContext>()
            .UseSqlite($"Data Source={GetDatabasePath(accountId)};Default Timeout=30")
            .Options;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _fileLock.Dispose();
    }
}
