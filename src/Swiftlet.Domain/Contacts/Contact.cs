using System.Buffers;

namespace Swiftlet.Domain.Contacts;

/// <summary>
/// An entry of the address book, shared by all accounts.
/// </summary>
public sealed class Contact
{
    /// <summary>
    /// The maximum length of an email address.
    /// </summary>
    public const int MaxEmailLength = 320;

    /// <summary>
    /// Characters that cannot appear in a stored email address.
    /// </summary>
    private static readonly SearchValues<char> ForbiddenEmailCharacters = SearchValues.Create(" \t\r\n<>,;\"，；");

    /// <summary>
    /// Gets the unique identifier of the contact.
    /// </summary>
    public Guid Id { get; private set; } = Guid.NewGuid();

    /// <summary>
    /// Gets the given name; empty when unknown.
    /// </summary>
    public string FirstName { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the family name; empty when unknown.
    /// </summary>
    public string LastName { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the full name composed of both parts; empty when unknown. Not stored.
    /// </summary>
    public string DisplayName => PersonName.Compose(FirstName, LastName);

    /// <summary>
    /// Composes the full name in a chosen order.
    /// </summary>
    /// <param name="order">Which part comes first.</param>
    /// <returns>The name; empty when unknown.</returns>
    public string FormatName(NameOrder order) => PersonName.Compose(FirstName, LastName, order);

    /// <summary>
    /// Gets the company; empty when unknown.
    /// </summary>
    public string Company { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the free-text note.
    /// </summary>
    public string Note { get; private set; } = string.Empty;

    /// <summary>
    /// Gets the email addresses; the first one is the preferred address. Never empty.
    /// </summary>
    public IReadOnlyList<string> Emails { get; private set; } = [];

    /// <summary>
    /// Gets the phone numbers.
    /// </summary>
    public IReadOnlyList<string> Phones { get; private set; } = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="Contact"/> class for persistence.
    /// </summary>
    private Contact()
    {
    }

    /// <summary>
    /// Creates a new contact.
    /// </summary>
    /// <param name="firstName">The given name; may be empty.</param>
    /// <param name="lastName">The family name; may be empty.</param>
    /// <param name="emails">The email addresses; at least one is required.</param>
    /// <param name="phones">The phone numbers.</param>
    /// <param name="company">The company.</param>
    /// <param name="note">The note.</param>
    /// <returns>The created contact.</returns>
    public static Contact Create(
        string? firstName,
        string? lastName,
        IEnumerable<string> emails,
        IEnumerable<string>? phones = null,
        string? company = null,
        string? note = null)
    {
        var contact = new Contact();
        contact.Update(firstName, lastName, emails, phones, company, note);
        return contact;
    }

    /// <summary>
    /// Determines whether a text can be stored as an email address.
    /// </summary>
    /// <param name="address">The text, without surrounding white space.</param>
    /// <returns><see langword="true"/> if it looks like an address.</returns>
    public static bool IsValidEmail(string? address)
    {
        if (string.IsNullOrEmpty(address) || address.Length > MaxEmailLength)
        {
            return false;
        }

        var at = address.LastIndexOf('@');
        return at > 0 && at < address.Length - 1 && !address.AsSpan().ContainsAny(ForbiddenEmailCharacters);
    }

    /// <summary>
    /// Replaces all details of the contact. Nothing changes when a value is rejected.
    /// </summary>
    /// <param name="firstName">The given name; may be empty.</param>
    /// <param name="lastName">The family name; may be empty.</param>
    /// <param name="emails">The email addresses; blank entries and repetitions are dropped, at least one is required.</param>
    /// <param name="phones">The phone numbers; blank entries and repetitions are dropped.</param>
    /// <param name="company">The company.</param>
    /// <param name="note">The note.</param>
    public void Update(
        string? firstName,
        string? lastName,
        IEnumerable<string> emails,
        IEnumerable<string>? phones = null,
        string? company = null,
        string? note = null)
    {
        ArgumentNullException.ThrowIfNull(emails);

        var addresses = Clean(emails, StringComparer.OrdinalIgnoreCase);
        if (addresses.Count == 0)
        {
            throw new ArgumentException("A contact needs at least one email address.", nameof(emails));
        }

        if (!addresses.TrueForAll(IsValidEmail))
        {
            throw new ArgumentException("An email address of the contact is not valid.", nameof(emails));
        }

        FirstName = (firstName ?? string.Empty).Trim();
        LastName = (lastName ?? string.Empty).Trim();
        Emails = addresses;
        Phones = Clean(phones ?? [], StringComparer.Ordinal);
        Company = (company ?? string.Empty).Trim();
        Note = (note ?? string.Empty).Trim();
    }

    /// <summary>
    /// Trims entries and drops blank and repeated ones, keeping the order.
    /// </summary>
    /// <param name="values">The raw values.</param>
    /// <param name="comparer">Decides which values are repetitions.</param>
    /// <returns>The cleaned values.</returns>
    private static List<string> Clean(IEnumerable<string> values, StringComparer comparer)
    {
        return values
            .Select(v => (v ?? string.Empty).Trim())
            .Where(v => v.Length > 0)
            .Distinct(comparer)
            .ToList();
    }
}
