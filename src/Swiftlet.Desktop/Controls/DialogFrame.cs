using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace Swiftlet.Desktop.Controls;

/// <summary>
/// The frame of a dialog: a title row drawn by the application, with the window title and a close button, above the
/// content. As the content of a window it replaces the system title bar; the system still draws the window border and
/// resizes the window.
/// </summary>
public sealed class DialogFrame : ContentControl
{
    /// <summary>
    /// Identifies the <see cref="Title"/> property.
    /// </summary>
    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<DialogFrame, string?>(nameof(Title));

    /// <summary>
    /// Identifies the <see cref="CloseCommand"/> property.
    /// </summary>
    public static readonly StyledProperty<ICommand?> CloseCommandProperty =
        AvaloniaProperty.Register<DialogFrame, ICommand?>(nameof(CloseCommand));

    /// <summary>
    /// The binding of <see cref="Title"/> to the title of the window.
    /// </summary>
    private IDisposable? _titleBinding;

    /// <summary>
    /// The close button of the applied template.
    /// </summary>
    private Button? _closeButton;

    /// <summary>
    /// Gets the text of the title row; it follows the title of the window.
    /// </summary>
    public string? Title
    {
        get => GetValue(TitleProperty);
        private set => SetValue(TitleProperty, value);
    }

    /// <summary>
    /// Gets or sets what the close button does. Without a command it closes the window; a dialog that may hold a close
    /// back sets one, because a close requested by code is not the close of the user.
    /// </summary>
    public ICommand? CloseCommand
    {
        get => GetValue(CloseCommandProperty);
        set => SetValue(CloseCommandProperty, value);
    }

    /// <inheritdoc />
    protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);

        // The logical tree is attached as soon as the content is assigned, so this happens before the window is shown.
        if (Parent is not Window window)
        {
            return;
        }

        window.WindowDecorations = WindowDecorations.BorderOnly;
        window.ExtendClientAreaToDecorationsHint = true;

        // There is no button to restore a maximized dialog, so a double click on the title row must not maximize it.
        window.CanMaximize = false;
        _titleBinding = Bind(TitleProperty, window.GetObservable(Window.TitleProperty));
    }

    /// <inheritdoc />
    protected override void OnDetachedFromLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        _titleBinding?.Dispose();
        _titleBinding = null;
        base.OnDetachedFromLogicalTree(e);
    }

    /// <inheritdoc />
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnApplyTemplate(e);
        if (_closeButton is not null)
        {
            _closeButton.Click -= OnCloseClick;
        }

        _closeButton = e.NameScope.Find<Button>("TitleCloseButton");
        if (_closeButton is not null)
        {
            _closeButton.Click += OnCloseClick;
        }
    }

    /// <summary>
    /// Runs the close command, or closes the window when there is none.
    /// </summary>
    /// <param name="sender">The close button.</param>
    /// <param name="e">The event data.</param>
    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        if (CloseCommand is { } command)
        {
            if (command.CanExecute(null))
            {
                command.Execute(null);
            }

            return;
        }

        (Parent as Window)?.Close();
    }
}
