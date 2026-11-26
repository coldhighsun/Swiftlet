using Avalonia.Controls;

namespace Swiftlet.Desktop.Views;

/// <summary>
/// The window shown before the main window while the databases are prepared.
/// </summary>
public partial class StartupWindow : Window
{
    /// <summary>
    /// Initializes a new instance of the <see cref="StartupWindow"/> class.
    /// </summary>
    public StartupWindow()
    {
        InitializeComponent();
    }
}
