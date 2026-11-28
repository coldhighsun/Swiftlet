using Avalonia.Media;

namespace Swiftlet.Desktop.ViewModels.Main;

/// <summary>
/// The account a message belongs to, as marked on its row in views that mix several accounts.
/// </summary>
/// <param name="name">The name shown, the same as in the folder tree.</param>
/// <param name="address">The email address of the account.</param>
/// <param name="color">The color of the account as <c>#RRGGBB</c>.</param>
public sealed class AccountTagViewModel(string name, string address, string color)
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
    /// Gets the color of the account as <c>#RRGGBB</c>.
    /// </summary>
    public string Color { get; } = color;

    /// <summary>
    /// Gets the brush of the account color; created on first use so view models stay creatable off the UI thread.
    /// </summary>
    public IBrush Brush => field ??= new SolidColorBrush(Avalonia.Media.Color.Parse(Color));
}
