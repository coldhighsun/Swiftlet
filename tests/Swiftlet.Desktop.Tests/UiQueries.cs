using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// Finds controls the way a user or assistive technology names them.
/// </summary>
internal static class UiQueries
{
    /// <summary>
    /// Gets the name of a button: its accessible name, or its text content.
    /// </summary>
    /// <param name="button">The button.</param>
    /// <returns>The name, or <see langword="null"/> when it has none.</returns>
    public static string? Caption(Button button)
    {
        return AutomationProperties.GetName(button) ?? button.Content as string;
    }

    /// <summary>
    /// Gets the names of all buttons below a control.
    /// </summary>
    /// <param name="root">The control.</param>
    /// <returns>The names.</returns>
    public static List<string?> ButtonCaptions(Control root)
    {
        return [.. root.GetVisualDescendants().OfType<Button>().Select(Caption)];
    }

    /// <summary>
    /// Finds the tree row of a node that carries a context menu.
    /// </summary>
    /// <param name="root">The control to search.</param>
    /// <param name="node">The node shown by the row.</param>
    /// <returns>The row.</returns>
    public static Control MenuOwner(Control root, object node)
    {
        return root.GetVisualDescendants().OfType<Control>().Single(c => c.ContextMenu is not null && ReferenceEquals(c.DataContext, node));
    }
}
