using Swiftlet.Application.Contacts;
using Swiftlet.Domain.Contacts;

namespace Swiftlet.Desktop.ViewModels.Contacts;

/// <summary>
/// A contact row of the contacts window.
/// </summary>
/// <param name="contact">The contact.</param>
public sealed class ContactItemViewModel(Contact contact)
{
    /// <summary>
    /// Gets the contact.
    /// </summary>
    public Contact Contact { get; } = contact;

    /// <summary>
    /// Gets the first line of the row: the name, or the address of a contact without one.
    /// </summary>
    public string Title => Contact.DisplayName.Length > 0 ? Contact.DisplayName : Contact.Emails[0];

    /// <summary>
    /// Gets the second line of the row.
    /// </summary>
    public string Subtitle => Contact.DisplayName.Length > 0 ? Contact.Emails[0] : Contact.Company;

    /// <summary>
    /// Gets a value indicating whether the row has a second line.
    /// </summary>
    public bool HasSubtitle => Subtitle.Length > 0;
}

/// <summary>
/// A collected address row of the contacts window.
/// </summary>
/// <param name="entry">The collected address.</param>
public sealed class CollectedItemViewModel(CollectedAddress entry)
{
    /// <summary>
    /// Gets the collected address.
    /// </summary>
    public CollectedAddress Entry { get; } = entry;

    /// <summary>
    /// Gets the first line of the row: the name, or the address when no name is known.
    /// </summary>
    public string Title => Entry.Name.Length > 0 ? Entry.Name : Entry.Address;

    /// <summary>
    /// Gets the second line of the row.
    /// </summary>
    public string Subtitle => Entry.Address;

    /// <summary>
    /// Gets a value indicating whether the row has a second line.
    /// </summary>
    public bool HasSubtitle => Entry.Name.Length > 0;
}
