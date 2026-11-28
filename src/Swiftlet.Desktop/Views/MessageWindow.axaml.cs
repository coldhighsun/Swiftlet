using Avalonia.Controls;
using Avalonia.Input;
using CommunityToolkit.Mvvm.Input;
using Swiftlet.Desktop.Controls;
using Swiftlet.Desktop.Hosting;
using Swiftlet.Desktop.ViewModels.Main;

namespace Swiftlet.Desktop.Views;

/// <summary>
/// A maximized window showing one message.
/// </summary>
public partial class MessageWindow : Window
{
    /// <summary>
    /// Shows the preview of the attachment the pointer rests on.
    /// </summary>
    private readonly AttachmentHoverController _attachmentHover;

    /// <summary>
    /// The view model whose close requests are followed, if any.
    /// </summary>
    private MessageWindowViewModel? _viewModel;

    /// <summary>
    /// Initializes a new instance of the <see cref="MessageWindow"/> class.
    /// </summary>
    public MessageWindow()
    {
        InitializeComponent();
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Escape), Command = new RelayCommand(Close) });
        _attachmentHover = AttachmentHoverBehavior.Attach(this, () => _viewModel?.Main);
    }

    /// <inheritdoc />
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is { } previous)
        {
            previous.CloseRequested -= OnCloseRequested;
        }

        _viewModel = DataContext as MessageWindowViewModel;
        if (_viewModel is { } current)
        {
            current.CloseRequested += OnCloseRequested;
        }
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _attachmentHover.Leave();
        if (_viewModel is { } viewModel)
        {
            viewModel.CloseRequested -= OnCloseRequested;
            viewModel.Detach();
        }
    }

    /// <summary>
    /// Closes the window after its message was archived or deleted.
    /// </summary>
    /// <param name="sender">The view model.</param>
    /// <param name="e">The event arguments.</param>
    private void OnCloseRequested(object? sender, EventArgs e) => Close();
}
