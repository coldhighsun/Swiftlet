using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Swiftlet.Application.Contacts;
using Swiftlet.Desktop.Formatting;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Services;

namespace Swiftlet.Desktop.ViewModels.Main;

/// <summary>
/// What can be done with an address shown in the reading pane.
/// </summary>
/// <param name="OpenContact">Shows the contact of an address, or offers to add one; gets the name that came with the mail and the address.</param>
/// <param name="Write">Opens a new message from an account to a recipient.</param>
/// <param name="Copy">Copies a text to the clipboard.</param>
public sealed record MessageAddressActions(
    Func<string, string, Task> OpenContact,
    Func<Guid, string, Task> Write,
    Func<string, Task> Copy);

/// <summary>
/// A sender or recipient in the header of the reading pane, with the menu behind it.
/// </summary>
public sealed class MessageAddressViewModel : ObservableObject
{
    /// <summary>
    /// The name that came with the mail; empty when there was none.
    /// </summary>
    private readonly string _headerName;

    /// <summary>
    /// The address book, if any.
    /// </summary>
    private readonly IContactDirectory? _contacts;

    /// <summary>
    /// The localizer of the menu texts.
    /// </summary>
    private readonly ILocalizer _localizer;

    /// <summary>
    /// Initializes a new instance of the <see cref="MessageAddressViewModel"/> class.
    /// </summary>
    /// <param name="accountId">The account that received the message.</param>
    /// <param name="headerName">The name that came with the mail; empty when there was none.</param>
    /// <param name="address">The address.</param>
    /// <param name="contacts">The address book, if any.</param>
    /// <param name="localizer">The localizer of the menu texts.</param>
    /// <param name="actions">What the menu does; <see langword="null"/> disables it.</param>
    public MessageAddressViewModel(
        Guid accountId,
        string headerName,
        string address,
        IContactDirectory? contacts,
        ILocalizer localizer,
        MessageAddressActions? actions)
    {
        ArgumentNullException.ThrowIfNull(headerName);
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(localizer);
        _headerName = headerName;
        _contacts = contacts;
        _localizer = localizer;
        Address = address;
        var usable = actions is not null && CanBeContact;
        ContactCommand = new AsyncRelayCommand(() => actions!.OpenContact(_headerName, Address), () => usable);
        WriteCommand = new AsyncRelayCommand(
            () => actions!.Write(accountId, RecipientList.Format(AddressText.Name(_contacts, _headerName, Address), Address)),
            () => usable);
        CopyCommand = new AsyncRelayCommand(() => actions!.Copy(Address), () => actions is not null);
    }

    /// <summary>
    /// Gets the address.
    /// </summary>
    public string Address { get; }

    /// <summary>
    /// Gets the text shown: <c>Name &lt;address&gt;</c>, or the bare address when no name is known.
    /// </summary>
    public string Text => AddressText.Full(_contacts, _headerName, Address);

    /// <summary>
    /// Gets a value indicating whether the address can be saved as a contact and written to.
    /// </summary>
    public bool CanBeContact => AddressText.CanBeContact(Address);

    /// <summary>
    /// Gets the menu text that adds the address to the address book, or shows its contact when it is already there.
    /// </summary>
    public string ContactText => _contacts?.Contains(Address) == true
        ? _localizer["Menu_Address_ViewContact"]
        : _localizer["Menu_Address_AddContact"];

    /// <summary>
    /// Gets the command that shows or adds the contact.
    /// </summary>
    public IAsyncRelayCommand ContactCommand { get; }

    /// <summary>
    /// Gets the command that writes a new message to the address.
    /// </summary>
    public IAsyncRelayCommand WriteCommand { get; }

    /// <summary>
    /// Gets the command that copies the address.
    /// </summary>
    public IAsyncRelayCommand CopyCommand { get; }

    /// <summary>
    /// Re-reads the texts after the address book or the language changed.
    /// </summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Text));
        OnPropertyChanged(nameof(ContactText));
    }
}
