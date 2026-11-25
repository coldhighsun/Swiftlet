namespace Swiftlet.Domain.Accounts;

/// <summary>
/// Persistence operations for mail accounts.
/// </summary>
public interface IAccountRepository
{
    /// <summary>
    /// Adds an account and saves it.
    /// </summary>
    /// <param name="account">The account to add.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the account is saved.</returns>
    Task AddAsync(MailAccount account, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets an account by identifier.
    /// </summary>
    /// <param name="id">The account identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The account, or <see langword="null"/> if not found.</returns>
    Task<MailAccount?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets all accounts ordered by creation time.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The accounts.</returns>
    Task<IReadOnlyList<MailAccount>> GetAllAsync(CancellationToken cancellationToken = default);
}
