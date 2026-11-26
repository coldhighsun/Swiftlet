namespace Swiftlet.Domain.Messages;

/// <summary>
/// Persistence and search operations for cached messages. Every account stores its mail in its own database, so
/// operations keyed by a folder or message identifier also take the owning account.
/// </summary>
public interface IMessageRepository
{
    /// <summary>
    /// Adds a message to its account's store and saves it.
    /// </summary>
    /// <param name="message">The message to add.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the message is saved.</returns>
    Task AddAsync(MailMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a message with its attachments and labels.
    /// </summary>
    /// <param name="accountId">The owning account.</param>
    /// <param name="id">The message identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The message, or <see langword="null"/> if not found.</returns>
    Task<MailMessage?> GetAsync(Guid accountId, Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves changes made to tracked messages.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when changes are saved.</returns>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the labels currently attached to a message (see <see cref="MailMessage.Labels"/>) to the label store.
    /// </summary>
    /// <param name="message">The message whose label links are saved.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the links are saved.</returns>
    Task SaveLabelsAsync(MailMessage message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds several messages in one transaction per account.
    /// </summary>
    /// <param name="messages">The messages to add.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the messages are saved.</returns>
    Task AddRangeAsync(IReadOnlyCollection<MailMessage> messages, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the highest cached UID in a folder.
    /// </summary>
    /// <param name="accountId">The owning account.</param>
    /// <param name="folderId">The folder.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The highest UID, or 0 if the folder has no cached messages.</returns>
    Task<uint> GetMaxUidAsync(Guid accountId, Guid folderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes all cached messages of a folder.
    /// </summary>
    /// <param name="accountId">The owning account.</param>
    /// <param name="folderId">The folder.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the messages are deleted.</returns>
    Task DeleteByFolderAsync(Guid accountId, Guid folderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines whether a message with the given UID is already cached in a folder.
    /// </summary>
    /// <param name="accountId">The owning account.</param>
    /// <param name="folderId">The folder.</param>
    /// <param name="uid">The IMAP UID.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> if the message exists.</returns>
    Task<bool> ExistsAsync(Guid accountId, Guid folderId, uint uid, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a page of messages in a folder, newest first.
    /// </summary>
    /// <param name="accountId">The owning account.</param>
    /// <param name="folderId">The folder.</param>
    /// <param name="skip">The number of messages to skip.</param>
    /// <param name="take">The maximum number of messages to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The messages.</returns>
    Task<IReadOnlyList<MailMessage>> GetByFolderAsync(
        Guid accountId,
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

    /// <summary>
    /// Gets a page of messages from the inbox folders of all accounts, newest first.
    /// </summary>
    /// <param name="skip">The number of messages to skip.</param>
    /// <param name="take">The maximum number of messages to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The messages.</returns>
    Task<IReadOnlyList<MailMessage>> GetUnifiedInboxAsync(
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a page of messages carrying a label, across all accounts, newest first.
    /// </summary>
    /// <param name="labelId">The label.</param>
    /// <param name="skip">The number of messages to skip.</param>
    /// <param name="take">The maximum number of messages to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The messages.</returns>
    Task<IReadOnlyList<MailMessage>> GetByLabelAsync(
        Guid labelId,
        int skip,
        int take,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes one cached message with its attachments and label links; does nothing if it does not exist.
    /// </summary>
    /// <param name="accountId">The owning account.</param>
    /// <param name="messageId">The message identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the message is deleted.</returns>
    Task DeleteAsync(Guid accountId, Guid messageId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the flag state of all messages of a folder without tracking or loading bodies, ordered by UID.
    /// </summary>
    /// <param name="accountId">The owning account.</param>
    /// <param name="folderId">The folder.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The flag states.</returns>
    Task<IReadOnlyList<MessageFlagState>> GetFlagStatesAsync(
        Guid accountId,
        Guid folderId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the flag state of the messages of a folder whose local change still has to be written to the server.
    /// </summary>
    /// <param name="accountId">The owning account.</param>
    /// <param name="folderId">The folder.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The pending flag states, ordered by UID.</returns>
    Task<IReadOnlyList<MessageFlagState>> GetPendingFlagStatesAsync(
        Guid accountId,
        Guid folderId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies server flags onto a message directly in the database, unless a local change is pending.
    /// </summary>
    /// <param name="accountId">The owning account.</param>
    /// <param name="messageId">The message identifier.</param>
    /// <param name="isRead">The server's read state.</param>
    /// <param name="isFlagged">The server's flagged state.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of updated rows; 0 if the message is pending or gone.</returns>
    Task<int> ApplyServerFlagsAsync(
        Guid accountId,
        Guid messageId,
        bool isRead,
        bool isFlagged,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the pending marker of pushed messages, but only where the stored values still equal the pushed ones.
    /// </summary>
    /// <param name="accountId">The owning account.</param>
    /// <param name="pushed">The states that were written to the server.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of rows whose marker was cleared.</returns>
    Task<int> ClearFlagSyncPendingAsync(
        Guid accountId,
        IReadOnlyCollection<MessageFlagState> pushed,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes several cached messages with their attachments and label links in bulk.
    /// </summary>
    /// <param name="accountId">The owning account.</param>
    /// <param name="messageIds">The message identifiers.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the messages are deleted.</returns>
    Task DeleteRangeAsync(Guid accountId, IReadOnlyCollection<Guid> messageIds, CancellationToken cancellationToken = default);
}
