using Swiftlet.Application.Abstractions;
using Swiftlet.Desktop.Services;
using Swiftlet.Desktop.ViewModels.Main;

namespace Swiftlet.Desktop.Hosting;

/// <summary>
/// Shows the preview of an attachment once the pointer has rested on it for a moment, and hides it when the pointer leaves.
/// </summary>
public sealed class AttachmentHoverController
{
    /// <summary>
    /// How long the pointer must rest on an attachment before anything is shown or downloaded.
    /// </summary>
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Loads the preview; returns <see langword="null"/> when it could not be built.
    /// </summary>
    private readonly Func<AttachmentItemViewModel, CancellationToken, Task<AttachmentPreview?>> _load;

    /// <summary>
    /// Creates what the preview window shows for an attachment.
    /// </summary>
    private readonly Func<AttachmentItemViewModel, AttachmentPreviewViewModel> _createViewModel;

    /// <summary>
    /// Shows and hides the preview window.
    /// </summary>
    private readonly IAttachmentPreviewPresenter _presenter;

    /// <summary>
    /// The delay before the preview appears.
    /// </summary>
    private readonly TimeSpan _delay;

    /// <summary>
    /// Cancels and releases the token source of the pending or running preview (kept as an action so that this class owns nothing disposable).
    /// </summary>
    private Action? _cancelCurrent;

    /// <summary>
    /// The attachment whose preview is pending or shown.
    /// </summary>
    private AttachmentItemViewModel? _current;

    /// <summary>
    /// Initializes a new instance of the <see cref="AttachmentHoverController"/> class.
    /// </summary>
    /// <param name="load">Loads the preview of an attachment; returns <see langword="null"/> on failure.</param>
    /// <param name="createViewModel">Creates the view model the preview window shows.</param>
    /// <param name="presenter">Shows and hides the preview window.</param>
    /// <param name="delay">The delay before the preview appears; optional.</param>
    public AttachmentHoverController(
        Func<AttachmentItemViewModel, CancellationToken, Task<AttachmentPreview?>> load,
        Func<AttachmentItemViewModel, AttachmentPreviewViewModel> createViewModel,
        IAttachmentPreviewPresenter presenter,
        TimeSpan? delay = null)
    {
        _load = load;
        _createViewModel = createViewModel;
        _presenter = presenter;
        _delay = delay ?? DefaultDelay;
    }

    /// <summary>
    /// Gets the task of the latest preview request, so tests can wait for it.
    /// </summary>
    internal Task Pending { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Tells the controller the pointer is over an attachment.
    /// </summary>
    /// <param name="attachment">The attachment.</param>
    /// <param name="anchor">The control of the attachment.</param>
    public void Enter(AttachmentItemViewModel attachment, object anchor)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        if (ReferenceEquals(_current, attachment))
        {
            return;
        }

        Leave();
        _current = attachment;
        var cts = new CancellationTokenSource();
        _cancelCurrent = () =>
        {
            cts.Cancel();
            cts.Dispose();
        };
        Pending = ShowAsync(attachment, anchor, cts.Token);
    }

    /// <summary>
    /// Tells the controller the pointer is no longer over an attachment; cancels the request and hides the preview.
    /// </summary>
    public void Leave()
    {
        if (_current is null)
        {
            return;
        }

        _current = null;
        _cancelCurrent?.Invoke();
        _cancelCurrent = null;
        _presenter.Hide();
    }

    /// <summary>
    /// Waits for the delay, shows the loading state and then the loaded preview.
    /// </summary>
    /// <param name="attachment">The attachment.</param>
    /// <param name="anchor">The control of the attachment.</param>
    /// <param name="cancellationToken">Cancelled when the pointer leaves.</param>
    /// <returns>A task that completes when the preview was shown or the request cancelled.</returns>
    private async Task ShowAsync(AttachmentItemViewModel attachment, object anchor, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_delay, cancellationToken);
            var viewModel = _createViewModel(attachment);
            _presenter.Show(viewModel, anchor);
            var preview = await _load(attachment, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            viewModel.Apply(preview);
        }
        catch (OperationCanceledException)
        {
            // The pointer left (or another attachment was entered) before the preview was ready; nothing to show.
        }
    }
}
