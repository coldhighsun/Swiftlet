using Avalonia.Media;
using Swiftlet.Desktop.Formatting;

namespace Swiftlet.Desktop.ViewModels.Main;

/// <summary>
/// The account a message belongs to, as marked on its row in views that mix several accounts.
/// </summary>
/// <param name="name">The name shown, the same as in the folder tree.</param>
/// <param name="address">The email address of the account.</param>
public sealed class AccountTagViewModel(string name, string address)
{
    /// <summary>
    /// Gets the name shown.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// Gets the email address of the account.
    /// </summary>
    public string Address { get; } = address;

    /// <summary>
    /// Gets the color of the account, the one of its avatar; created on first use so view models stay creatable off the
    /// UI thread.
    /// </summary>
    public IBrush Brush => field ??= new SolidColorBrush(Color.Parse(Avatar.Color(Address)));
}
