using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Swiftlet.Desktop.ViewModels.Contacts;

/// <summary>
/// One email address or phone number field of the contact form.
/// </summary>
public sealed partial class ContactValueViewModel : ObservableObject
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ContactValueViewModel"/> class.
    /// </summary>
    /// <param name="value">The initial value.</param>
    /// <param name="remove">The action removing the field from the form.</param>
    /// <param name="makePrimary">The action making the field the preferred one; does nothing when omitted.</param>
    public ContactValueViewModel(
        string value,
        Action<ContactValueViewModel> remove,
        Action<ContactValueViewModel>? makePrimary = null)
    {
        Value = value;
        RemoveCommand = new RelayCommand(() => remove(this));
        MakePrimaryCommand = new RelayCommand(() => makePrimary?.Invoke(this));
    }

    /// <summary>
    /// Gets or sets the typed value.
    /// </summary>
    [ObservableProperty]
    public partial string Value { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this is the preferred address, which is the first one.
    /// </summary>
    [ObservableProperty]
    public partial bool IsPrimary { get; set; }

    /// <summary>
    /// Gets the command removing the field from the form.
    /// </summary>
    public IRelayCommand RemoveCommand { get; }

    /// <summary>
    /// Gets the command making this the preferred address.
    /// </summary>
    public IRelayCommand MakePrimaryCommand { get; }
}
