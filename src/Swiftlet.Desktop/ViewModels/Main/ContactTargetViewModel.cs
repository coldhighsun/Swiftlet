using System.Windows.Input;

namespace Swiftlet.Desktop.ViewModels.Main;

/// <summary>
/// An entry of the "add to contacts" menu: the sender or one recipient of the selected message.
/// </summary>
/// <param name="header">The menu text.</param>
/// <param name="command">The command showing or adding the contact of that address.</param>
public sealed class ContactTargetViewModel(string header, ICommand command)
{
    /// <summary>
    /// Gets the menu text.
    /// </summary>
    public string Header { get; } = header;

    /// <summary>
    /// Gets the command showing or adding the contact of that address.
    /// </summary>
    public ICommand Command { get; } = command;
}
