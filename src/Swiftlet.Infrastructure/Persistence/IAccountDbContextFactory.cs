namespace Swiftlet.Infrastructure.Persistence;

/// <summary>
/// Creates the contexts of the per-account mail databases and removes a database when its account is deleted.
/// </summary>
public interface IAccountDbContextFactory
{
    /// <summary>
    /// Creates a context for an account's database, creating and initializing the database on first use.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A new context that the caller must dispose.</returns>
    Task<AccountDbContext> CreateAsync(Guid accountId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an account's database files; does nothing if they do not exist.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the files are gone.</returns>
    Task DeleteAsync(Guid accountId, CancellationToken cancellationToken = default);
}
