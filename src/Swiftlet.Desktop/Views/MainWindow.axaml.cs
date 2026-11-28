using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Swiftlet.Desktop.Controls;
using Avalonia.VisualTree;
using Swiftlet.Desktop.Hosting;
using Swiftlet.Desktop.ViewModels.Main;

namespace Swiftlet.Desktop.Views;

/// <summary>
/// The main application window.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// How often the view reloads the cache, so messages synchronized in the background appear.
    /// </summary>
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The timer that periodically refreshes the view.
    /// </summary>
    private DispatcherTimer? _refreshTimer;

    /// <summary>
    /// Shows the preview of the attachment the pointer rests on.
    /// </summary>
    private readonly AttachmentHoverController _attachmentHover;

    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindow"/> class.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
        AccountTreeBehavior.Attach(FolderTree, AccountDropLine, (node, index) => (DataContext as MainWindowViewModel)?.MoveAccountAsync(node, index) ?? Task.CompletedTask);
        Opened += OnOpenedAsync;
        Closed += OnClosed;

        // Tunnelling runs before the list changes its selection, which is what the view model has to know in advance.
        MessageList.AddHandler(PointerPressedEvent, OnMessagePressed, RoutingStrategies.Tunnel);

        _attachmentHover = AttachmentHoverBehavior.Attach(this, () => DataContext as MainWindowViewModel);
    }

    /// <summary>
    /// Loads the data once the window is shown and starts the periodic refresh.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private async void OnOpenedAsync(object? sender, EventArgs e)
    {
        // Opened is raised again whenever a window hidden in the tray is shown, but loading happens only once.
        Opened -= OnOpenedAsync;
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        _refreshTimer = new DispatcherTimer(RefreshInterval, DispatcherPriority.Background, async (_, _) => await viewModel.RefreshAsync());
        _refreshTimer.Start();
        await viewModel.InitializeAsync();
    }

    /// <summary>
    /// Asks the view model for the next page of messages when the list is scrolled to within one screen of its end.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private async void OnMessageListScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.Source is not ScrollViewer scroller || DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var remaining = scroller.Extent.Height - scroller.Offset.Y - scroller.Viewport.Height;
        if (remaining <= scroller.Viewport.Height)
        {
            // LoadMoreAsync reports failures in the status line itself; the event handler boundary only needs the call.
            await viewModel.LoadMoreAsync();
        }
    }

    /// <summary>
    /// Opens the window of a double-clicked message. The first click has selected it already, so it counts as read; a
    /// double click that leaves another message selected (the scroll bar, taking a row out of the selection) does nothing.
    /// </summary>
    /// <param name="sender">The message list.</param>
    /// <param name="e">The event arguments.</param>
    private void OnMessageListDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel
            && (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is MessageItemViewModel item
            && ReferenceEquals(item, viewModel.SelectedMessage))
        {
            viewModel.OpenMessageWindow(item);
        }
    }

    /// <summary>
    /// Tells the view model how a message row was pressed: a right-click selects it without marking it read.
    /// </summary>
    /// <param name="sender">The message list.</param>
    /// <param name="e">The event arguments.</param>
    private void OnMessagePressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel
            || (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is not MessageItemViewModel item)
        {
            return;
        }

        var point = e.GetCurrentPoint(this).Properties;
        if (point.IsRightButtonPressed)
        {
            viewModel.BeginContextSelection(item);
        }
        else if (point.IsLeftButtonPressed && e.KeyModifiers == KeyModifiers.None)
        {
            viewModel.OpenClickedMessage(item);
        }
    }

    /// <summary>
    /// Fills the "move to" and "add to contacts" submenus for the selection the context menu was opened on.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private void OnMessageMenuOpening(object? sender, CancelEventArgs e)
    {
        var viewModel = DataContext as MainWindowViewModel;
        viewModel?.RefreshMoveTargets();
        viewModel?.RefreshContactTargets();
        viewModel?.RefreshPriorityMenu();
        viewModel?.RefreshReadMenu();
    }

    /// <summary>
    /// Stops the periodic refresh and the sync-triggered refreshes.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private void OnClosed(object? sender, EventArgs e)
    {
        _refreshTimer?.Stop();
        _attachmentHover.Leave();
        (DataContext as MainWindowViewModel)?.Dispose();
    }
}
