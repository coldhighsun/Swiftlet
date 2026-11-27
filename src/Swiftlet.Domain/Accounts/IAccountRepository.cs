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

    /// <summary>
    /// Saves the changes made to an account that was read from this repository.
    /// </summary>
    /// <param name="account">The changed account.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the changes are saved.</returns>
    Task UpdateAsync(MailAccount account, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an account together with its folders, messages, attachments and label links.
    /// </summary>
    /// <param name="id">The account identifier; an unknown id is ignored.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the account is deleted.</returns>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
