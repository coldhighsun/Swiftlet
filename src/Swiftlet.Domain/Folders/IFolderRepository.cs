namespace Swiftlet.Domain.Folders;

/// <summary>
/// Persistence operations for mail folders.
/// </summary>
public interface IFolderRepository
{
    /// <summary>
    /// Adds a folder and saves it.
    /// </summary>
    /// <param name="folder">The folder to add.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the folder is saved.</returns>
    Task AddAsync(MailFolder folder, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a folder by identifier.
    /// </summary>
    /// <param name="accountId">The owning account.</param>
    /// <param name="id">The folder identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The folder, or <see langword="null"/> if not found.</returns>
    Task<MailFolder?> GetAsync(Guid accountId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the folders of an account ordered by full name.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The folders.</returns>
    Task<IReadOnlyList<MailFolder>> GetByAccountAsync(Guid accountId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves changes made to a tracked folder.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when changes are saved.</returns>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);
}
