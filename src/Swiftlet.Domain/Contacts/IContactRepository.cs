namespace Swiftlet.Domain.Contacts;

/// <summary>
/// Persistence of the address book: the contacts, the addresses mail was sent to and the addresses the user removed
/// from the collected ones.
/// </summary>
public interface IContactRepository
{
    /// <summary>
    /// Gets all contacts, ordered by name (by address for contacts without one).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The tracked contacts.</returns>
    Task<IReadOnlyList<Contact>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a contact.
    /// </summary>
    /// <param name="id">The contact identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The tracked contact, or <see langword="null"/> if not found.</returns>
    Task<Contact?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds contacts and saves them together with all pending changes to tracked contacts, in one transaction.
    /// </summary>
    /// <param name="contacts">The contacts to add.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when everything is saved.</returns>
    Task AddRangeAsync(IReadOnlyCollection<Contact> contacts, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves changes made to tracked contacts.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the changes are saved.</returns>
    Task SaveChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes a contact; does nothing if it does not exist.
    /// </summary>
    /// <param name="id">The contact identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the contact is deleted.</returns>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the addresses mail was sent to from this application.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>One entry per account and address.</returns>
    Task<IReadOnlyList<SentRecipient>> GetSentRecipientsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Remembers recipients of a sent message. A known entry gets the new time, and the new name unless that is empty.
    /// </summary>
    /// <param name="recipients">The recipients, with lower-case addresses.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the recipients are saved.</returns>
    Task RecordSentRecipientsAsync(IReadOnlyCollection<SentRecipient> recipients, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the addresses the user removed from the collected ones.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The addresses, in lower case.</returns>
    Task<IReadOnlyList<string>> GetDismissedAddressesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Keeps an address out of the collected ones from now on.
    /// </summary>
    /// <param name="address">The address; stored in lower case.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the address is saved.</returns>
    Task DismissAddressAsync(string address, CancellationToken cancellationToken = default);
}
