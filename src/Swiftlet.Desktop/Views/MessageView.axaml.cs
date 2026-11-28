using Avalonia;
using Avalonia.Controls;
using Swiftlet.Desktop.ViewModels.Ai;
using Swiftlet.Desktop.ViewModels.Main;

namespace Swiftlet.Desktop.Views;

/// <summary>
/// Shows one whole message: header, attachments and body.
/// </summary>
public partial class MessageView : UserControl
{
    /// <summary>
    /// Identifies the <see cref="AiAssist"/> property.
    /// </summary>
    public static readonly StyledProperty<AiAssistViewModel?> AiAssistProperty =
        AvaloniaProperty.Register<MessageView, AiAssistViewModel?>(nameof(AiAssist));

    /// <summary>
    /// The message whose attachments were listed last.
    /// </summary>
    private Guid? _attachmentsMessageId;

    /// <summary>
    /// Initializes a new instance of the <see cref="MessageView"/> class.
    /// </summary>
    public MessageView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Gets or sets the AI panel shown above the body; <see langword="null"/> where there is none.
    /// </summary>
    public AiAssistViewModel? AiAssist
    {
        get => GetValue(AiAssistProperty);
        set => SetValue(AiAssistProperty, value);
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
}
