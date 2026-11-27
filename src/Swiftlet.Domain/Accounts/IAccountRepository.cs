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
    /// Gets all accounts in the order of the folder tree (<see cref="MailAccount.SortOrder"/>, then creation time).
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
    /// Stores the order of the accounts in the folder tree: each listed account gets its position in the list.
    /// </summary>
    /// <param name="orderedIds">The account identifiers in the wanted order; unknown ones are ignored.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the order is saved.</returns>
    Task ReorderAsync(IReadOnlyList<Guid> orderedIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores whether an account node is expanded in the folder tree, without touching other columns.
    /// </summary>
    /// <param name="id">The account identifier; an unknown id is ignored.</param>
    /// <param name="expanded">Whether the node is expanded.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the state is saved.</returns>
    Task SetExpandedAsync(Guid id, bool expanded, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes an account together with its folders, messages, attachments and label links.
    /// </summary>
    /// <param name="id">The account identifier; an unknown id is ignored.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the account is deleted.</returns>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}
