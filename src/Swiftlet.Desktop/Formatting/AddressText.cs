using Swiftlet.Desktop.Services;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Contacts;

namespace Swiftlet.Desktop.Formatting;

/// <summary>
/// How a person behind an email address is shown. The name of the address book wins over the one that came with the mail.
/// </summary>
public static class AddressText
{
    /// <summary>
    /// Gets the name to use for an address: that of its contact, otherwise the one that came with the mail.
    /// </summary>
    /// <param name="contacts">The address book; <see langword="null"/> when there is none.</param>
    /// <param name="name">The name that came with the mail; may be empty.</param>
    /// <param name="address">The address.</param>
    /// <returns>The name; empty when neither source has one.</returns>
    public static string Name(IContactDirectory? contacts, string? name, string address)
    {
        return contacts?.NameOf(address) ?? (string.IsNullOrWhiteSpace(name) ? string.Empty : name);
    }

    /// <summary>
    /// Gets <c>Name &lt;address&gt;</c>, or the bare address when no name is known.
    /// </summary>
    /// <param name="contacts">The address book; <see langword="null"/> when there is none.</param>
    /// <param name="name">The name that came with the mail; may be empty.</param>
    /// <param name="address">The address.</param>
    /// <returns>The text.</returns>
    public static string Full(IContactDirectory? contacts, string? name, string address)
    {
        var shown = Name(contacts, name, address);
        return shown.Length == 0 || string.Equals(shown, address, StringComparison.OrdinalIgnoreCase)
            ? address
            : $"{shown} <{address}>";
    }

    /// <summary>
    /// Gets the one-line text of a sender in narrow places: <c>Name &lt;address&gt;</c> for a named contact, otherwise the
    /// name that came with the mail, falling back to the address.
    /// </summary>
    /// <param name="contacts">The address book; <see langword="null"/> when there is none.</param>
    /// <param name="name">The name that came with the mail; may be empty.</param>
    /// <param name="address">The address.</param>
    /// <returns>The text.</returns>
    public static string Compact(IContactDirectory? contacts, string? name, string address)
    {
        if (contacts?.NameOf(address) is not null)
        {
            return Full(contacts, name, address);
        }

        return string.IsNullOrWhiteSpace(name) ? address : name;
    }

    /// <summary>
    /// Gets how an own account is named: the name the user gave it, otherwise its address.
    /// </summary>
    /// <param name="account">The account.</param>
    /// <returns>The name.</returns>
    public static string AccountName(MailAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return account.HasCustomDisplayName ? account.DisplayName : account.EmailAddress;
    }

    /// <summary>
    /// Gets a value indicating whether an address can be saved as a contact. Messages without a usable address carry a
    /// placeholder in the reserved <c>invalid</c> domain.
    /// </summary>
    /// <param name="address">The address.</param>
    /// <returns><see langword="true"/> if it can.</returns>
    public static bool CanBeContact(string address)
    {
        return Contact.IsValidEmail(address) && !address.EndsWith("@invalid", StringComparison.OrdinalIgnoreCase);
    }
}
