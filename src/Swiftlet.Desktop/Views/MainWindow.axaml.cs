using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Swiftlet.Desktop.Controls;
using Avalonia.VisualTree;
using Swiftlet.Application.Abstractions;
using Swiftlet.Desktop.Hosting;
using Swiftlet.Desktop.Services;
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
    /// The message whose attachments the reading pane listed last.
    /// </summary>
    private Guid? _attachmentsMessageId;

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

        _attachmentHover = new AttachmentHoverController(
            LoadAttachmentPreviewAsync,
            attachment => ((MainWindowViewModel)DataContext!).CreateAttachmentPreview(attachment),
            new AvaloniaAttachmentPreviewPresenter());

        // The attachment buttons live in a data template, so the pointer is tracked on the window and the button under it is looked up.
        AddHandler(PointerMovedEvent, OnPointerMovedOverAttachments, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerPressedEvent, (_, _) => _attachmentHover.Leave(), RoutingStrategies.Tunnel, handledEventsToo: true);
        // Scrolling moves the attachments under a resting pointer without a pointer movement.
        AddHandler(PointerWheelChangedEvent, (_, _) => _attachmentHover.Leave(), RoutingStrategies.Tunnel, handledEventsToo: true);
        PointerExited += (_, _) => _attachmentHover.Leave();
        Deactivated += (_, _) => _attachmentHover.Leave();
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
    /// Shows the attachments of another message from the top. The reading pane reuses its controls, which would keep the
    /// scroll position; a reload of the same message keeps it on purpose.
    /// </summary>
    /// <param name="sender">The scroll viewer of the attachments.</param>
    /// <param name="e">The event arguments.</param>
    private void OnAttachmentsDataContextChanged(object? sender, EventArgs e)
    {
        if (sender is ScrollViewer { DataContext: MessageItemViewModel item } scroller && item.Message.Id != _attachmentsMessageId)
        {
            _attachmentsMessageId = item.Message.Id;
            scroller.Offset = default;
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
    /// Loads the preview of an attachment through the current view model.
    /// </summary>
    /// <param name="attachment">The attachment.</param>
    /// <param name="cancellationToken">Cancelled when the pointer leaves the attachment.</param>
    /// <returns>The preview, or <see langword="null"/> when none could be loaded.</returns>
    private Task<AttachmentPreview?> LoadAttachmentPreviewAsync(AttachmentItemViewModel attachment, CancellationToken cancellationToken)
    {
        return DataContext is MainWindowViewModel viewModel
            ? viewModel.LoadAttachmentPreviewAsync(attachment, cancellationToken)
            : Task.FromResult<AttachmentPreview?>(null);
    }

    /// <summary>
    /// Starts or stops the attachment preview depending on whether the pointer is over the button of an attachment that
    /// opens it; the download button beside it has its own tooltip.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private void OnPointerMovedOverAttachments(object? sender, PointerEventArgs e)
    {
        var button = (e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true);
        if (button?.DataContext is AttachmentItemViewModel attachment && button.Classes.Contains("attachment"))
        {
            _attachmentHover.Enter(attachment, button);
        }
        else
        {
            _attachmentHover.Leave();
        }
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
