using System.Globalization;
using System.Text;
using Avalonia.Media;
using Swiftlet.Application.Contacts;
using Swiftlet.Desktop.Formatting;
using Swiftlet.Domain.Contacts;

namespace Swiftlet.Desktop.ViewModels.Contacts;

/// <summary>
/// An email address or phone number shown on the contact card.
/// </summary>
/// <param name="Value">The address or number.</param>
/// <param name="IsPrimary">Whether it is marked as the preferred address among several.</param>
public sealed record ContactDetailRow(string Value, bool IsPrimary);

/// <summary>
/// A contact of the contacts window: its row in the list and the card showing its details.
/// </summary>
/// <param name="contact">The contact.</param>
public sealed class ContactItemViewModel(Contact contact)
{
    /// <summary>
    /// The group of the contacts whose title does not start with a Latin letter.
    /// </summary>
    public const string OtherGroup = "#";

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

    /// <summary>
    /// Gets the letter the contact is listed under.
    /// </summary>
    public string GroupKey => GroupOf(Title);

    /// <summary>
    /// Gets a value indicating whether the row starts its group and therefore shows the heading.
    /// </summary>
    public bool IsGroupStart { get; init; }

    /// <summary>
    /// Gets the initial shown in the round avatar.
    /// </summary>
    public string Initial => Avatar.Initial(Contact.DisplayName, Contact.Emails[0]);

    /// <summary>
    /// Gets the background of the avatar.
    /// </summary>
    public IBrush AvatarBrush => field ??= new SolidColorBrush(Color.Parse(Avatar.Color(Contact.Emails[0])));

    /// <summary>
    /// Gets the company shown on the card.
    /// </summary>
    public string Company => Contact.Company;

    /// <summary>
    /// Gets a value indicating whether the contact has a company.
    /// </summary>
    public bool HasCompany => Contact.Company.Length > 0;

    /// <summary>
    /// Gets the note shown on the card.
    /// </summary>
    public string Note => Contact.Note;

    /// <summary>
    /// Gets a value indicating whether the contact has a note.
    /// </summary>
    public bool HasNote => Contact.Note.Length > 0;

    /// <summary>
    /// Gets the email addresses of the card; the preferred one is marked when there are several.
    /// </summary>
    public IReadOnlyList<ContactDetailRow> EmailRows =>
        [.. Contact.Emails.Select((email, index) => new ContactDetailRow(email, index == 0 && Contact.Emails.Count > 1))];

    /// <summary>
    /// Gets the phone numbers of the card.
    /// </summary>
    public IReadOnlyList<ContactDetailRow> PhoneRows =>
        [.. Contact.Phones.Select(phone => new ContactDetailRow(phone, false))];

    /// <summary>
    /// Gets a value indicating whether the contact has a phone number.
    /// </summary>
    public bool HasPhones => Contact.Phones.Count > 0;

    /// <summary>
    /// Gets the group of a title: its first letter without accents, in upper case, when that is a Latin letter.
    /// </summary>
    /// <param name="title">The title.</param>
    /// <returns><c>A</c> to <c>Z</c>, or <see cref="OtherGroup"/>.</returns>
    public static string GroupOf(string title)
    {
        ArgumentNullException.ThrowIfNull(title);

        var text = title.TrimStart();
        if (text.Length == 0)
        {
            return OtherGroup;
        }

        // Decomposing separates an accent from its letter. Names of other scripts, Chinese included, are not
        // transliterated.
        var first = StringInfo.GetNextTextElement(text).Normalize(NormalizationForm.FormD)[0];
        var letter = char.ToUpperInvariant(first);
        return char.IsAsciiLetterUpper(letter) ? letter.ToString() : OtherGroup;
    }
}

/// <summary>
/// A collected address of the contacts window.
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

    /// <summary>
    /// Gets the initial shown in the round avatar.
    /// </summary>
    public string Initial => Avatar.Initial(Entry.Name, Entry.Address);

    /// <summary>
    /// Gets the background of the avatar.
    /// </summary>
    public IBrush AvatarBrush => field ??= new SolidColorBrush(Color.Parse(Avatar.Color(Entry.Address)));
}
