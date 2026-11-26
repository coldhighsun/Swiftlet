using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Swiftlet.Desktop.Services;

/// <summary>
/// Builds the window that asks the user to confirm a destructive action.
/// </summary>
public static class ConfirmWindow
{
    /// <summary>
    /// Creates the window; showing it as a dialog yields <see langword="true"/> only when the confirming button was pressed.
    /// </summary>
    /// <param name="title">The window title.</param>
    /// <param name="message">The question.</param>
    /// <param name="okText">The caption of the confirming button.</param>
    /// <param name="cancelText">The caption of the cancel button.</param>
    /// <returns>The window.</returns>
    public static Window Create(string title, string message, string okText, string cancelText)
    {
        // Cancel, not the confirming button, answers Enter: the confirmed action may be destructive.
        var ok = new Button { Content = okText, MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Classes.Add("danger");
        var cancel = new Button
        {
            Content = cancelText,
            IsCancel = true,
            IsDefault = true,
            MinWidth = 88,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { ok, cancel },
        };
        var panel = new StackPanel
        {
            Margin = new Avalonia.Thickness(24),
            Spacing = 24,
            Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, buttons },
        };
        var window = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel,
        };
        ok.Click += (_, _) => window.Close(true);
        cancel.Click += (_, _) => window.Close(false);
        return window;
    }
}
