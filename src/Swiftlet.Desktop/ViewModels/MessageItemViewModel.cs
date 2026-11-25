using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Services;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Desktop.ViewModels;

/// <summary>
/// An attachment shown in the reading pane.
/// </summary>
public sealed class AttachmentItemViewModel
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AttachmentItemViewModel"/> class.
    /// </summary>
    /// <param name="attachment">The attachment metadata.</param>
    /// <param name="save">The action invoked to save the attachment.</param>
    public AttachmentItemViewModel(MailAttachment attachment, Func<AttachmentItemViewModel, Task> save)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(save);
        Attachment = attachment;
        SaveCommand = new AsyncRelayCommand(() => save(this));
    }

    /// <summary>
    /// Gets the attachment metadata.
    /// </summary>
    public MailAttachment Attachment { get; }

    /// <summary>
    /// Gets the text shown on the attachment button.
    /// </summary>
    public string Label => $"📎 {Attachment.FileName} ({FileSizeFormatter.Format(Attachment.Size)})";

    /// <summary>
    /// Gets the command that saves the attachment to disk.
    /// </summary>
    public IAsyncRelayCommand SaveCommand { get; }
}

/// <summary>
/// A message row in the list and its content in the reading pane.
/// </summary>
public sealed partial class MessageItemViewModel : ObservableObject
{
    /// <summary>
    /// The localizer used for the placeholder subject.
    /// </summary>
    private readonly ILocalizer _localizer;

    /// <summary>
    /// Initializes a new instance of the <see cref="MessageItemViewModel"/> class.
    /// </summary>
    /// <param name="message">The cached message.</param>
    /// <param name="saveAttachment">The action invoked to save one of the attachments.</param>
    /// <param name="localizer">The localizer used for the placeholder subject.</param>
    public MessageItemViewModel(MailMessage message, Func<AttachmentItemViewModel, Task> saveAttachment, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(message);
        _localizer = localizer;
        Message = message;
        IsRead = message.IsRead;
        IsFlagged = message.IsFlagged;
        Attachments = [.. message.Attachments.OrderBy(a => a.Ordinal).Select(a => new AttachmentItemViewModel(a, saveAttachment))];
    }

    /// <summary>
    /// Gets the underlying message.
    /// </summary>
    public MailMessage Message { get; }

    /// <summary>
    /// Gets the attachments ordered as on the server.
    /// </summary>
    public IReadOnlyList<AttachmentItemViewModel> Attachments { get; }

    /// <summary>
    /// Gets or sets a value indicating whether the message is read.
    /// </summary>
    [ObservableProperty]
    public partial bool IsRead { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the message is flagged.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FlagGlyph))]
    public partial bool IsFlagged { get; set; }

    /// <summary>
    /// Gets the message identifier.
    /// </summary>
    public Guid Id => Message.Id;

    /// <summary>
    /// Gets the subject, or a placeholder when empty.
    /// </summary>
    public string Subject => string.IsNullOrWhiteSpace(Message.Subject) ? _localizer["Message_NoSubject"] : Message.Subject;

    /// <summary>
    /// Re-reads language-dependent text after the UI language changed.
    /// </summary>
    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(Subject));
    }

    /// <summary>
    /// Gets the sender's display name, falling back to the address.
    /// </summary>
    public string FromDisplay => string.IsNullOrWhiteSpace(Message.FromName) ? Message.FromAddress : Message.FromName;

    /// <summary>
    /// Gets the sender as <c>Name &lt;address&gt;</c>.
    /// </summary>
    public string FromFull => string.IsNullOrWhiteSpace(Message.FromName)
        ? Message.FromAddress
        : $"{Message.FromName} <{Message.FromAddress}>";

    /// <summary>
    /// Gets the recipients.
    /// </summary>
    public string Recipients => Message.Recipients;

    /// <summary>
    /// Gets the sent time in the local time zone.
    /// </summary>
    public string SentAtText => Message.SentAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Gets the one-line body preview.
    /// </summary>
    public string Preview => Message.Preview;

    /// <summary>
    /// Gets the plain-text body.
    /// </summary>
    public string BodyText => Message.BodyText;

    /// <summary>
    /// Gets a value indicating whether the message has an HTML body.
    /// </summary>
    public bool HasHtml => !string.IsNullOrWhiteSpace(Message.BodyHtml);

    /// <summary>
    /// Gets a value indicating whether the message has attachments.
    /// </summary>
    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>
    /// Gets the glyph representing the flagged state.
    /// </summary>
    public string FlagGlyph => IsFlagged ? "★" : "☆";
}
