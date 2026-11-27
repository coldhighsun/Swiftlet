using Mediator;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Contacts;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Application.Contacts;

/// <summary>
/// An address mail was sent to that is not part of any contact.
/// </summary>
/// <param name="Address">The address.</param>
/// <param name="Name">The name used with it; empty when unknown.</param>
/// <param name="LastUsedAt">When mail was last sent to it.</param>
public sealed record CollectedAddress(string Address, string Name, DateTimeOffset LastUsedAt);

/// <summary>
/// The whole address book.
/// </summary>
/// <param name="Contacts">The contacts, ordered by name.</param>
/// <param name="Collected">The collected addresses, most recently used first.</param>
public sealed record AddressBook(IReadOnlyList<Contact> Contacts, IReadOnlyList<CollectedAddress> Collected);

/// <summary>
/// Gets the contacts and the collected addresses.
/// </summary>
public sealed record GetAddressBookQuery : IQuery<AddressBook>;

/// <summary>
/// The two parts of the name of a contact; the view decides in which order they are shown.
/// </summary>
/// <param name="FirstName">The given name; may be empty.</param>
/// <param name="LastName">The family name; may be empty.</param>
public sealed record ContactName(string FirstName, string LastName);

/// <summary>
/// Gets every address that belongs to a contact with the name of that contact. Both parts are empty for a contact without
/// a name. Addresses are compared ignoring case. Unlike <see cref="GetAddressBookQuery"/> it reads the contacts only, so it
/// is cheap enough to run whenever names are shown.
/// </summary>
public sealed record GetContactNamesQuery : IQuery<IReadOnlyDictionary<string, ContactName>>;

/// <summary>
/// Handles <see cref="GetContactNamesQuery"/>.
/// </summary>
/// <param name="contacts">The contact repository.</param>
public sealed class GetContactNamesQueryHandler(IContactRepository contacts)
    : IQueryHandler<GetContactNamesQuery, IReadOnlyDictionary<string, ContactName>>
{
    /// <inheritdoc />
    public async ValueTask<IReadOnlyDictionary<string, ContactName>> Handle(
        GetContactNamesQuery query,
        CancellationToken cancellationToken)
    {
        var names = new Dictionary<string, ContactName>(StringComparer.OrdinalIgnoreCase);
        foreach (var contact in await contacts.GetAllAsync(cancellationToken))
        {
            foreach (var address in contact.Emails)
            {
                // Should two contacts share an address, a name wins over none, and otherwise the first one.
                if (!names.TryGetValue(address, out var known) || known is { FirstName: "", LastName: "" })
                {
                    names[address] = new ContactName(contact.FirstName, contact.LastName);
                }
            }
        }

        return names;
    }
}

/// <summary>
/// Handles <see cref="GetAddressBookQuery"/>. The collected addresses are derived on every request from the cached Sent
/// folders and from the recipients remembered when sending, so nothing has to be updated during synchronization.
/// </summary>
/// <param name="contacts">The contact repository.</param>
/// <param name="messages">The message repository.</param>
/// <param name="accounts">The account repository.</param>
public sealed class GetAddressBookQueryHandler(
    IContactRepository contacts,
    IMessageRepository messages,
    IAccountRepository accounts) : IQueryHandler<GetAddressBookQuery, AddressBook>
{
    /// <summary>
    /// The sender placeholder of messages without a usable address.
    /// </summary>
    private const string PlaceholderAddress = "unknown@invalid";

    /// <inheritdoc />
    public async ValueTask<AddressBook> Handle(GetAddressBookQuery query, CancellationToken cancellationToken)
    {
        var all = await contacts.GetAllAsync(cancellationToken);

        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { PlaceholderAddress };
        excluded.UnionWith(all.SelectMany(c => c.Emails));
        excluded.UnionWith(await contacts.GetDismissedAddressesAsync(cancellationToken));
        excluded.UnionWith((await accounts.GetAllAsync(cancellationToken)).Select(a => a.EmailAddress));

        var found = new Dictionary<string, CollectedAddress>(StringComparer.OrdinalIgnoreCase);
        foreach (var sent in await contacts.GetSentRecipientsAsync(cancellationToken))
        {
            Merge(found, excluded, sent.Address, sent.Name, sent.LastUsedAt);
        }

        foreach (var message in await messages.GetSentFolderRecipientsAsync(cancellationToken))
        {
            foreach (var entry in RecipientList.Split(message.Recipients))
            {
                var recipient = RecipientList.Parse(entry);
                Merge(found, excluded, recipient.Address, recipient.Name, message.SentAt);
            }
        }

        var collected = found.Values
            .OrderByDescending(a => a.LastUsedAt)
            .ThenBy(a => a.Address, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new AddressBook(all, collected);
    }

    /// <summary>
    /// Adds one use of an address, keeping the latest time and the latest non-empty name.
    /// </summary>
    /// <param name="found">The addresses collected so far.</param>
    /// <param name="excluded">The addresses that are never collected.</param>
    /// <param name="address">The address.</param>
    /// <param name="name">The name used with it.</param>
    /// <param name="usedAt">When it was used.</param>
    private static void Merge(
        Dictionary<string, CollectedAddress> found,
        HashSet<string> excluded,
        string address,
        string name,
        DateTimeOffset usedAt)
    {
        if (!Contact.IsValidEmail(address) || excluded.Contains(address))
        {
            return;
        }

        if (!found.TryGetValue(address, out var known))
        {
            found[address] = new CollectedAddress(address, name, usedAt);
            return;
        }

        var isNewer = usedAt > known.LastUsedAt;
        var keptName = name.Length > 0 && (isNewer || known.Name.Length == 0) ? name : known.Name;
        found[address] = known with { Name = keptName, LastUsedAt = isNewer ? usedAt : known.LastUsedAt };
    }
}
