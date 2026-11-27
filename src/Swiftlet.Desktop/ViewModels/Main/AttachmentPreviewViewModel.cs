using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Swiftlet.Application.Abstractions;
using Swiftlet.Desktop.Localization;

namespace Swiftlet.Desktop.ViewModels.Main;

/// <summary>
/// What the attachment preview window shows: the file, then a thumbnail, some text or a message.
/// </summary>
public sealed partial class AttachmentPreviewViewModel : ObservableObject
{
    /// <summary>
    /// The localizer used for the status messages.
    /// </summary>
    private readonly ILocalizer _localizer;

    /// <summary>
    /// The thumbnail, if the preview is an image.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImage))]
    private Bitmap? _image;

    /// <summary>
    /// The text or entry list, if the preview is text.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasText))]
    private string? _text;

    /// <summary>
    /// The message shown instead of content (loading, not available, too large).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string? _message;

    /// <summary>
    /// Initializes a new instance of the <see cref="AttachmentPreviewViewModel"/> class showing the loading message.
    /// </summary>
    /// <param name="attachment">The previewed attachment.</param>
    /// <param name="localizer">The localizer used for the status messages.</param>
    public AttachmentPreviewViewModel(AttachmentItemViewModel attachment, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        _localizer = localizer;
        FileName = attachment.FileName;
        Details = attachment.SizeText;
        Message = _localizer["Preview_Loading"];
    }

    /// <summary>
    /// Gets the file name.
    /// </summary>
    public string FileName { get; }

    /// <summary>
    /// Gets the readable file size.
    /// </summary>
    public string Details { get; }

    /// <summary>
    /// Gets a value indicating whether a thumbnail is shown.
    /// </summary>
    public bool HasImage => Image is not null;

    /// <summary>
    /// Gets a value indicating whether text is shown.
    /// </summary>
    public bool HasText => !string.IsNullOrEmpty(Text);

    /// <summary>
    /// Gets a value indicating whether a status message is shown.
    /// </summary>
    public bool HasMessage => !string.IsNullOrEmpty(Message);

    /// <summary>
    /// Shows the result of loading the preview.
    /// </summary>
    /// <param name="preview">The preview, or <see langword="null"/> when it could not be loaded.</param>
    public void Apply(AttachmentPreview? preview)
    {
        Image = null;
        Text = null;
        Message = null;
        switch (preview?.Kind)
        {
            case AttachmentPreviewKind.Image when preview.Image is { Length: > 0 } bytes:
                ShowImage(bytes);
                break;
            case AttachmentPreviewKind.Text when !string.IsNullOrEmpty(preview.Text):
                Text = preview.Truncated ? $"{preview.Text}\n…" : preview.Text;
                break;
            case AttachmentPreviewKind.Listing when !string.IsNullOrEmpty(preview.Text):
                Text = preview.MoreEntries > 0
                    ? $"{preview.Text}\n{_localizer.Format("Preview_MoreEntries", preview.MoreEntries)}"
                    : preview.Text;
                break;
            case AttachmentPreviewKind.TooLarge:
                Message = _localizer["Preview_TooLarge"];
                break;
            default:
                Message = _localizer["Preview_NotAvailable"];
                break;
        }
    }

    /// <summary>
    /// Decodes the thumbnail; undecodable data shows the "not available" message.
    /// </summary>
    /// <param name="bytes">The PNG or JPEG bytes.</param>
    private void ShowImage(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            Image = new Bitmap(stream);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or NotSupportedException)
        {
            // The bytes come from a decoder that just produced them, so this is rare; the preview is optional anyway.
            Message = _localizer["Preview_NotAvailable"];
        }
    }
}
