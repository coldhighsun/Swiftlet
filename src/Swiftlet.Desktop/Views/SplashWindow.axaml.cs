using Avalonia.Controls;

namespace Swiftlet.Desktop.Views;

/// <summary>
/// The splash screen shown while the application starts.
/// </summary>
public partial class SplashWindow : Window
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SplashWindow"/> class.
    /// </summary>
    public SplashWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Keeps the user from closing the splash screen, which would end the application in the middle of its startup.
    /// Closing from code and the end of the system session go through.
    /// </summary>
    /// <param name="e">The event data.</param>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.CloseReason == WindowCloseReason.WindowClosing && !e.IsProgrammatic)
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }
}
