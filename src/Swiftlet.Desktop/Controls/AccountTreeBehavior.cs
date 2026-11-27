using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Swiftlet.Desktop.ViewModels.Main;

namespace Swiftlet.Desktop.Controls;

/// <summary>
/// Adds the account-specific behavior to the folder tree: the user can drag an account node up or down to change the
/// order of the accounts, and the expanded state of an account node follows its view model.
/// </summary>
public sealed class AccountTreeBehavior
{
    /// <summary>
    /// The distance in pixels the pointer must move with the button held before a drag starts.
    /// </summary>
    private const double DragThreshold = 6;

    /// <summary>
    /// The data format of an account being dragged; the dragged node itself is kept in <see cref="_dragging"/>.
    /// </summary>
    private static readonly DataFormat<string> AccountFormat = DataFormat.CreateStringApplicationFormat("swiftlet-account");

    /// <summary>
    /// The tree the behavior is attached to.
    /// </summary>
    private readonly TreeView _tree;

    /// <summary>
    /// The line showing where the dragged account will be dropped.
    /// </summary>
    private readonly Control _dropLine;

    /// <summary>
    /// Moves an account node to a new position among the accounts.
    /// </summary>
    private readonly Func<AccountNodeViewModel, int, Task> _move;

    /// <summary>
    /// The press that may turn into a drag, or <see langword="null"/>.
    /// </summary>
    private PointerPressedEventArgs? _pressed;

    /// <summary>
    /// The account node under the pressed pointer, or <see langword="null"/>.
    /// </summary>
    private AccountNodeViewModel? _pressedNode;

    /// <summary>
    /// The position of the press, relative to the tree.
    /// </summary>
    private Point _pressPoint;

    /// <summary>
    /// The account node being dragged, or <see langword="null"/> when no drag of ours is running.
    /// </summary>
    private AccountNodeViewModel? _dragging;

    /// <summary>
    /// Initializes a new instance of the <see cref="AccountTreeBehavior"/> class.
    /// </summary>
    /// <param name="tree">The tree to attach to.</param>
    /// <param name="dropLine">The control used as drop indicator; it must sit in the same grid as the tree.</param>
    /// <param name="move">Moves an account node to a new index among the accounts.</param>
    private AccountTreeBehavior(TreeView tree, Control dropLine, Func<AccountNodeViewModel, int, Task> move)
    {
        _tree = tree;
        _dropLine = dropLine;
        _move = move;
    }

    /// <summary>
    /// Attaches the behavior to a tree.
    /// </summary>
    /// <param name="tree">The tree to attach to.</param>
    /// <param name="dropLine">The control used as drop indicator; it must sit in the same grid as the tree.</param>
    /// <param name="move">Moves an account node to a new index among the accounts.</param>
    /// <returns>The attached behavior.</returns>
    public static AccountTreeBehavior Attach(TreeView tree, Control dropLine, Func<AccountNodeViewModel, int, Task> move)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(dropLine);
        ArgumentNullException.ThrowIfNull(move);

        var behavior = new AccountTreeBehavior(tree, dropLine, move);
        tree.ContainerPrepared += behavior.OnContainerPrepared;

        // Tunnelling sees the press before the item selects itself, and nothing here marks the event as handled.
        tree.AddHandler(InputElement.PointerPressedEvent, behavior.OnPointerPressed, RoutingStrategies.Tunnel);
        tree.AddHandler(InputElement.PointerMovedEvent, behavior.OnPointerMovedAsync, RoutingStrategies.Tunnel);
        tree.AddHandler(InputElement.PointerReleasedEvent, behavior.OnPointerReleased, RoutingStrategies.Tunnel);
        DragDrop.SetAllowDrop(tree, true);
        tree.AddHandler(DragDrop.DragOverEvent, behavior.OnDragOver);
        tree.AddHandler(DragDrop.DragLeaveEvent, behavior.OnDragLeave);
        tree.AddHandler(DragDrop.DropEvent, behavior.OnDropAsync);
        return behavior;
    }

    /// <summary>
    /// Binds the expanded state of an account item to its view model; other items keep the default.
    /// </summary>
    /// <param name="sender">The tree.</param>
    /// <param name="e">The event arguments.</param>
    private void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container is TreeViewItem item && item.DataContext is AccountNodeViewModel)
        {
            item.Bind(
                TreeViewItem.IsExpandedProperty,
                new Binding(nameof(AccountNodeViewModel.IsExpanded)) { Mode = BindingMode.TwoWay });
        }
    }

    /// <summary>
    /// Remembers a press on an account row, the possible start of a drag.
    /// </summary>
    /// <param name="sender">The tree.</param>
    /// <param name="e">The event arguments.</param>
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _pressed = null;
        _pressedNode = null;
        if (!e.GetCurrentPoint(_tree).Properties.IsLeftButtonPressed)
        {
            return;
        }

        // The nearest data context decides: a folder row inside an expanded account is not the account.
        if (FindNode(e.Source) is { } node)
        {
            _pressed = e;
            _pressedNode = node;
            _pressPoint = e.GetPosition(_tree);
        }
    }

    /// <summary>
    /// Starts the drag once the pointer has moved far enough from the press.
    /// </summary>
    /// <param name="sender">The tree.</param>
    /// <param name="e">The event arguments.</param>
    private async void OnPointerMovedAsync(object? sender, PointerEventArgs e)
    {
        if (_pressed is not { } pressed || _pressedNode is not { } node || _dragging is not null)
        {
            return;
        }

        var distance = e.GetPosition(_tree) - _pressPoint;
        if (Math.Abs(distance.X) < DragThreshold && Math.Abs(distance.Y) < DragThreshold)
        {
            return;
        }

        _pressed = null;
        _pressedNode = null;
        _dragging = node;
        try
        {
            var item = new DataTransferItem();
            item.Set(AccountFormat, node.Account.Id.ToString());
            var transfer = new DataTransfer();
            transfer.Add(item);
            await DragDrop.DoDragDropAsync(pressed, transfer, DragDropEffects.Move);
        }
        finally
        {
            _dragging = null;
            _dropLine.IsVisible = false;
        }
    }

    /// <summary>
    /// Forgets a press that ended without a drag.
    /// </summary>
    /// <param name="sender">The tree.</param>
    /// <param name="e">The event arguments.</param>
    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _pressed = null;
        _pressedNode = null;
    }

    /// <summary>
    /// Shows where the dragged account would be dropped.
    /// </summary>
    /// <param name="sender">The tree.</param>
    /// <param name="e">The event arguments.</param>
    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (_dragging is null || !e.DataTransfer.Contains(AccountFormat))
        {
            e.DragEffects = DragDropEffects.None;
            _dropLine.IsVisible = false;
            return;
        }

        var (_, lineY) = FindSlot(e.GetPosition(_tree).Y);
        _dropLine.Margin = new Thickness(_tree.Margin.Left, lineY - 1, _tree.Margin.Right, 0);
        _dropLine.IsVisible = true;
        e.DragEffects = DragDropEffects.Move;
        e.Handled = true;
    }

    /// <summary>
    /// Hides the drop indicator when the pointer leaves the tree.
    /// </summary>
    /// <param name="sender">The tree.</param>
    /// <param name="e">The event arguments.</param>
    private void OnDragLeave(object? sender, DragEventArgs e)
    {
        _dropLine.IsVisible = false;
    }

    /// <summary>
    /// Moves the dragged account to the slot under the pointer.
    /// </summary>
    /// <param name="sender">The tree.</param>
    /// <param name="e">The event arguments.</param>
    private async void OnDropAsync(object? sender, DragEventArgs e)
    {
        _dropLine.IsVisible = false;
        if (_dragging is not { } node || !e.DataTransfer.Contains(AccountFormat))
        {
            return;
        }

        e.Handled = true;
        var (before, _) = FindSlot(e.GetPosition(_tree).Y);
        var accounts = AccountNodes();
        var oldIndex = accounts.IndexOf(node);

        // The slot is a position among all accounts; once the dragged one is taken out, later slots shift up by one.
        var newIndex = before > oldIndex ? before - 1 : before;
        await _move(node, newIndex);
    }

    /// <summary>
    /// Finds the account the pointer is over, looking at the nearest data context of a visual.
    /// </summary>
    /// <param name="source">The event source.</param>
    /// <returns>The account node, or <see langword="null"/> when the pointer is not over an account row.</returns>
    private static AccountNodeViewModel? FindNode(object? source)
    {
        for (var visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is StyledElement { DataContext: { } context })
            {
                return context as AccountNodeViewModel;
            }
        }

        return null;
    }

    /// <summary>
    /// Lists the account nodes in tree order.
    /// </summary>
    /// <returns>The account nodes.</returns>
    private List<AccountNodeViewModel> AccountNodes()
    {
        return [.. _tree.Items.OfType<AccountNodeViewModel>()];
    }

    /// <summary>
    /// Finds the gap between accounts closest to a vertical position.
    /// </summary>
    /// <param name="y">The position, relative to the tree.</param>
    /// <returns>The index of the account the dragged one would be put before (the account count for the end), and the
    /// vertical position of the gap.</returns>
    private (int Before, double LineY) FindSlot(double y)
    {
        var accounts = AccountNodes();
        double lineY = 0;
        for (var i = 0; i < accounts.Count; i++)
        {
            if (_tree.ContainerFromItem(accounts[i]) is not TreeViewItem container)
            {
                continue;
            }

            var top = container.TranslatePoint(default, _tree)?.Y ?? 0;
            var header = container.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "PART_Header");
            var headerHeight = header?.Bounds.Height ?? container.Bounds.Height;
            if (y < top + (headerHeight / 2))
            {
                return (i, top);
            }

            // An expanded account owns its folders as well; the gap after it lies below them.
            lineY = top + container.Bounds.Height;
        }

        return (accounts.Count, lineY);
    }
}
