using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Contacts;
using Swiftlet.Application.Sending;
using Swiftlet.Desktop.Formatting;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Services;
using Swiftlet.Domain.Contacts;
using Swiftlet.Domain.Accounts;

namespace Swiftlet.Desktop.ViewModels.Compose;

/// <summary>
/// A file chosen to be attached to a message being composed.
/// </summary>
public sealed class ComposeAttachmentViewModel
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ComposeAttachmentViewModel"/> class.
    /// </summary>
    /// <param name="path">The full path of the file.</param>
    /// <param name="remove">The action invoked to remove the attachment from the message.</param>
    public ComposeAttachmentViewModel(string path, Action<ComposeAttachmentViewModel> remove)
    {
        Path = path;
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    /// <summary>
    /// Gets the full path of the file.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Gets the command that removes the attachment from the message.
    /// </summary>
    public IRelayCommand RemoveCommand { get; }

    /// <summary>
    /// Gets the file name.
    /// </summary>
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>
    /// Gets the text shown in the attachment list.
    /// </summary>
    public string Label
    {
        get
        {
            var info = new FileInfo(Path);
            return info.Exists ? $"{FileName} ({FileSizeFormatter.Format(info.Length)})" : FileName;
        }
    }
}

/// <summary>
/// The view model of a compose window.
/// </summary>
public sealed partial class ComposeViewModel : ObservableObject
{
    /// <summary>
    /// The request dispatcher.
    /// </summary>
    private readonly IRequestDispatcher _dispatcher;

    /// <summary>
    /// The dialog service.
    /// </summary>
    private readonly IDialogService _dialogs;

    /// <summary>
    /// The localizer.
    /// </summary>
    private readonly ILocalizer _localizer;

    /// <summary>
    /// The Message-ID being replied to, if any.
    /// </summary>
    private readonly string? _inReplyTo;

    /// <summary>
    /// The logger, if any.
    /// </summary>
    private readonly ILogger? _logger;

    /// <summary>
    /// The order of contact names in the suggestions.
    /// </summary>
    private readonly NameOrder _nameOrder;

    /// <summary>
    /// Initializes a new instance of the <see cref="ComposeViewModel"/> class.
    /// </summary>
    /// <param name="dispatcher">The request dispatcher.</param>
    /// <param name="dialogs">The dialog service.</param>
    /// <param name="localizer">The localizer.</param>
    /// <param name="accounts">The accounts that can send.</param>
    /// <param name="request">The initial values.</param>
    /// <param name="logger">The logger; optional.</param>
    /// <param name="nameOrder">The order of contact names in the suggestions.</param>
    public ComposeViewModel(
        IRequestDispatcher dispatcher,
        IDialogService dialogs,
        ILocalizer localizer,
        IReadOnlyList<MailAccount> accounts,
        ComposeRequest request,
        ILogger<ComposeViewModel>? logger = null,
        NameOrder nameOrder = NameOrder.Auto)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(request);
        _dispatcher = dispatcher;
        _dialogs = dialogs;
        _localizer = localizer;
        _inReplyTo = request.InReplyTo;
        _logger = logger;
        _nameOrder = nameOrder;
        Suggestions = [];
        Accounts = accounts;
        Title = request.Mode switch
        {
            ComposeMode.Reply => localizer["Compose_TitleReply"],
            ComposeMode.Forward => localizer["Compose_TitleForward"],
            _ => localizer["Compose_TitleNew"],
        };
        SelectedAccount = accounts.FirstOrDefault(a => a.Id == request.AccountId) ?? (accounts.Count > 0 ? accounts[0] : null);
        To = request.To;
        Cc = string.Empty;
        Bcc = string.Empty;
        Subject = request.Subject;
        Body = request.Body;
        ErrorMessage = string.Empty;
    }

    /// <summary>
    /// Raised when the window should close; the argument is <see langword="true"/> if the message was sent.
    /// </summary>
    public event EventHandler<bool>? CloseRequested;

    /// <summary>
    /// Gets the window title.
    /// </summary>
    public string Title { get; }

    /// <summary>
    /// Gets the accounts that can send.
    /// </summary>
    public IReadOnlyList<MailAccount> Accounts { get; }

    /// <summary>
    /// Gets the attachments added so far.
    /// </summary>
    public ObservableCollection<ComposeAttachmentViewModel> Attachments { get; } = [];

    /// <summary>
    /// Gets or sets the addresses suggested while recipients are typed.
    /// </summary>
    [ObservableProperty]
    public partial IReadOnlyList<RecipientSuggestion> Suggestions { get; set; }

    /// <summary>
    /// Gets or sets the sending account.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial MailAccount? SelectedAccount { get; set; }

    /// <summary>
    /// Gets or sets the To recipients, separated by commas or semicolons.
    /// </summary>
    [ObservableProperty]
    public partial string To { get; set; }

    /// <summary>
    /// Gets or sets the Cc recipients.
    /// </summary>
    [ObservableProperty]
    public partial string Cc { get; set; }

    /// <summary>
    /// Gets or sets the Bcc recipients.
    /// </summary>
    [ObservableProperty]
    public partial string Bcc { get; set; }

    /// <summary>
    /// Gets or sets the subject.
    /// </summary>
    [ObservableProperty]
    public partial string Subject { get; set; }

    /// <summary>
    /// Gets or sets the plain-text body.
    /// </summary>
    [ObservableProperty]
    public partial string Body { get; set; }

    /// <summary>
    /// Gets or sets the last error to show to the user.
    /// </summary>
    [ObservableProperty]
    public partial string ErrorMessage { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the message is being sent.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial bool IsSending { get; set; }

    /// <summary>
    /// Loads the recipient suggestions from the address book.
    /// </summary>
    /// <returns>A task that completes when the suggestions are available or loading failed.</returns>
    public async Task InitializeAsync()
    {
        try
        {
            var book = await _dispatcher.SendAsync(new GetAddressBookQuery());
            Suggestions = RecipientSuggestions.From(book, _nameOrder);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Suggestions are a convenience: the message can be written and sent without them.
            _logger?.LogWarning(ex, "The recipient suggestions could not be loaded");
        }
    }

    /// <summary>
    /// Sends the message and closes the window on success.
    /// </summary>
    /// <returns>A task that completes when the message was sent or the attempt failed.</returns>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        ErrorMessage = string.Empty;
        var to = RecipientList.Split(To);
        var cc = RecipientList.Split(Cc);
        var bcc = RecipientList.Split(Bcc);
        var all = to.Concat(cc).Concat(bcc).ToList();
        if (all.Count == 0)
        {
            ErrorMessage = _localizer["Compose_NoRecipients"];
            return;
        }

        var invalid = all.FirstOrDefault(a => !RecipientList.Parse(a).Address.Contains('@', StringComparison.Ordinal));
        if (invalid is not null)
        {
            ErrorMessage = _localizer.Format("Compose_InvalidRecipient", invalid);
            return;
        }

        IsSending = true;
        try
        {
            var attachments = new List<OutgoingAttachment>();
            foreach (var attachment in Attachments)
            {
                var content = await File.ReadAllBytesAsync(attachment.Path);
                attachments.Add(new OutgoingAttachment(attachment.FileName, "application/octet-stream", content));
            }

            var message = new OutgoingMessage
            {
                To = to,
                Cc = cc,
                Bcc = bcc,
                Subject = Subject,
                TextBody = Body,
                Attachments = attachments,
                InReplyTo = _inReplyTo,
            };
            await _dispatcher.SendAsync(new SendMessageCommand(SelectedAccount!.Id, message));
            CloseRequested?.Invoke(this, true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Keep the window open with the draft intact so the user can retry or fix the problem.
            ErrorMessage = _localizer.Format("Compose_SendFailed", ex.Message);
        }
        finally
        {
            IsSending = false;
        }
    }

    /// <summary>
    /// Lets the user pick files and adds them as attachments.
    /// </summary>
    /// <returns>A task that completes when the files have been added.</returns>
    [RelayCommand]
    private async Task AddAttachmentsAsync()
    {
        foreach (var path in await _dialogs.PickFilesAsync())
        {
            if (Attachments.All(a => a.Path != path))
            {
                Attachments.Add(new ComposeAttachmentViewModel(path, a => Attachments.Remove(a)));
            }
        }
    }

    /// <summary>
    /// Closes the window without sending.
    /// </summary>
    [RelayCommand]
    private void Cancel()
    {
        CloseRequested?.Invoke(this, false);
    }

    /// <summary>
    /// Gets a value indicating whether the message can be sent now.
    /// </summary>
    private bool CanSend => !IsSending && SelectedAccount is not null;
}
