namespace Swiftlet.Domain.Messages;

/// <summary>
/// Persistence and search operations for cached messages.
/// </summary>
public interface IMessageRepository
{
    /// <summary>
    /// Adds a message and saves it.
    /// </summary>
    /// <param name="message">The message to add.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the message is saved.</returns>
    Task AddAsync(MailMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a message with its attachments.
    /// </summary>
    /// <param name="id">The message identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The message, or <see langword="null"/> if not found.</returns>
    Task<MailMessage?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves changes made to tracked messages.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when changes are saved.</returns>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds several messages in one transaction.
    /// </summary>
    /// <param name="messages">The messages to add.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the messages are saved.</returns>
    Task AddRangeAsync(IReadOnlyCollection<MailMessage> messages, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the highest cached UID in a folder.
    /// </summary>
    /// <param name="folderId">The folder.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The highest UID, or 0 if the folder has no cached messages.</returns>
    Task<uint> GetMaxUidAsync(Guid folderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all cached messages of a folder.
    /// </summary>
    /// <param name="folderId">The folder.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the messages are deleted.</returns>
    Task DeleteByFolderAsync(Guid folderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines whether a message with the given UID is already cached in a folder.
    /// </summary>
    /// <param name="folderId">The folder.</param>
    /// <param name="uid">The IMAP UID.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> if the message exists.</returns>
    Task<bool> ExistsAsync(Guid folderId, uint uid, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a page of messages in a folder, newest first.
    /// </summary>
    /// <param name="folderId">The folder.</param>
    /// <param name="skip">The number of messages to skip.</param>
    /// <param name="take">The maximum number of messages to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The messages.</returns>
    Task<IReadOnlyList<MailMessage>> GetByFolderAsync(
        Guid folderId,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches cached messages by subject, sender and body text, newest first.
    /// </summary>
    /// <param name="query">The user-entered search text; all terms must match.</param>
    /// <param name="accountId">Restricts the search to one account, or <see langword="null"/> for all accounts.</param>
    /// <param name="take">The maximum number of results.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The matching messages.</returns>
    Task<IReadOnlyList<MailMessage>> SearchAsync(
        string query,
        Guid? accountId,
        int take,
        CancellationToken cancellationToken = default);
}
