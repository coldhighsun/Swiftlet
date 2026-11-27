using Avalonia.Controls;
using Swiftlet.Desktop.ViewModels.Contacts;

namespace Swiftlet.Desktop.Views;

/// <summary>
/// The dialog for managing contacts and collected addresses.
/// </summary>
public partial class ContactsWindow : Window
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ContactsWindow"/> class.
    /// </summary>
    public ContactsWindow()
    {
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        // Only a close by the user is held back: quitting the application and signing out must go through.
        if (e is { CloseReason: WindowCloseReason.WindowClosing, IsProgrammatic: false }
            && DataContext is ContactsViewModel { HasUnsavedChanges: true } viewModel)
        {
            // The view model asks, and requests the close again when the changes may be dropped.
            e.Cancel = true;
            viewModel.CloseCommand.Execute(null);
        }

        base.OnClosing(e);
    }
}
