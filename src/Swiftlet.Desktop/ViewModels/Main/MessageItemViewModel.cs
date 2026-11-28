using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Contacts;
using Swiftlet.Application.Queries;
using Swiftlet.Desktop.Formatting;
using Swiftlet.Desktop.Html;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Services;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Desktop.ViewModels.Main;

/// <summary>
/// An attachment shown in the reading pane.
/// </summary>
public sealed class AttachmentItemViewModel
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AttachmentItemViewModel"/> class.
    /// </summary>
    /// <param name="accountId">The account that owns the attachment's message.</param>
    /// <param name="attachment">The attachment metadata.</param>
    /// <param name="open">The action invoked to open the attachment.</param>
    /// <param name="save">The action invoked to save the attachment.</param>
    public AttachmentItemViewModel(
        Guid accountId,
        MailAttachment attachment,
        Func<AttachmentItemViewModel, Task> open,
        Func<AttachmentItemViewModel, Task> save)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(save);
        AccountId = accountId;
        Attachment = attachment;
        OpenCommand = new AsyncRelayCommand(() => open(this));
        SaveCommand = new AsyncRelayCommand(() => save(this));
    }

    /// <summary>
    /// Gets the account that owns the attachment's message.
    /// </summary>
    public Guid AccountId { get; }

    /// <summary>
    /// Gets the attachment metadata.
    /// </summary>
    public MailAttachment Attachment { get; }

    /// <summary>
    /// Gets the name and size, which also names the attachment button for assistive technology.
    /// </summary>
    public string Label => $"{FileName} ({SizeText})";

    /// <summary>
    /// Gets the file name.
    /// </summary>
    public string FileName => Attachment.FileName;

    /// <summary>
    /// Gets the readable file size.
    /// </summary>
    public string SizeText => FileSizeFormatter.Format(Attachment.Size);

    /// <summary>
    /// Gets the command that opens the attachment with the system's program for it.
    /// </summary>
    public IAsyncRelayCommand OpenCommand { get; }

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
    /// The action that saves a remote image rule; <see langword="null"/> when rules cannot be saved.
    /// </summary>
    private readonly Func<MessageItemViewModel, string, Task>? _rememberRemoteImages;

    /// <summary>
    /// The clock the list time is relative to.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// The address book whose names are shown; <see langword="null"/> when there is none.
    /// </summary>
    private readonly IContactDirectory? _contacts;

    /// <summary>
    /// What the menus of the header addresses do; <see langword="null"/> disables them.
    /// </summary>
    private readonly MessageAddressActions? _addressActions;

    /// <summary>
    /// The header entry of the sender, once asked for.
    /// </summary>
    private MessageAddressViewModel? _senderAddress;

    /// <summary>
    /// The header entries of the recipients, once asked for.
    /// </summary>
    private MessageAddressViewModel[]? _recipientAddresses;

    /// <summary>
    /// Initializes a new instance of the <see cref="MessageItemViewModel"/> class.
    /// </summary>
    /// <param name="message">The cached message.</param>
    /// <param name="openAttachment">The action invoked to open one of the attachments.</param>
    /// <param name="saveAttachment">The action invoked to save one of the attachments.</param>
    /// <param name="localizer">The localizer used for the placeholder subject.</param>
    /// <param name="rememberRemoteImages">The action invoked to save a rule that always allows remote images (the rule is an address or a domain).</param>
    /// <param name="timeProvider">The clock the list time is relative to; optional.</param>
    /// <param name="contacts">The address book whose names are shown; optional.</param>
    /// <param name="addressActions">What the menus of the header addresses do; optional.</param>
    public MessageItemViewModel(
        MailMessage message,
        Func<AttachmentItemViewModel, Task> openAttachment,
        Func<AttachmentItemViewModel, Task> saveAttachment,
        ILocalizer localizer,
        Func<MessageItemViewModel, string, Task>? rememberRemoteImages = null,
        TimeProvider? timeProvider = null,
        IContactDirectory? contacts = null,
        MessageAddressActions? addressActions = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        _localizer = localizer;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _rememberRemoteImages = rememberRemoteImages;
        _contacts = contacts;
        _addressActions = addressActions;
        Message = message;
        IsRead = message.IsRead;
        IsFlagged = message.IsFlagged;
        Attachments = [.. message.Attachments.OrderBy(a => a.Ordinal).Select(a => new AttachmentItemViewModel(message.AccountId, a, openAttachment, saveAttachment))];
        Labels = BuildChips(message);
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
    /// Gets the labels attached to the message.
    /// </summary>
    public IReadOnlyList<LabelChipViewModel> Labels { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the message has labels.
    /// </summary>
    public bool HasLabels => Labels.Count > 0;

    /// <summary>
    /// Gets the text marking a high or low priority, or <see langword="null"/>; a normal priority is not marked.
    /// </summary>
    public string? PriorityText => Message.AiPriority switch
    {
        MessagePriority.High => _localizer["Priority_High"],
        MessagePriority.Low => _localizer["Priority_Low"],
        _ => null,
    };

    /// <summary>
    /// Gets a value indicating whether the priority is high.
    /// </summary>
    public bool IsHighPriority => Message.AiPriority == MessagePriority.High;

    /// <summary>
    /// Shows the priority again after it changed.
    /// </summary>
    public void RefreshPriority()
    {
        OnPropertyChanged(nameof(PriorityText));
        OnPropertyChanged(nameof(IsHighPriority));
    }

    /// <summary>
    /// Rebuilds the label chips after the labels of the message changed.
    /// </summary>
    public void RefreshLabels()
    {
        Labels = BuildChips(Message);
        OnPropertyChanged(nameof(Labels));
        OnPropertyChanged(nameof(HasLabels));
    }

    /// <summary>
    /// Copies the state that can change after a sync (read, starred, labels) from a freshly loaded copy of the same message,
    /// so a list refresh can keep this object (and the page shown for it) instead of replacing it.
    /// </summary>
    /// <param name="fresh">The freshly loaded message with the same identifier.</param>
    public void Update(MailMessage fresh)
    {
        ArgumentNullException.ThrowIfNull(fresh);
        IsRead = fresh.IsRead;
        IsFlagged = fresh.IsFlagged;
        if (!ReferenceEquals(fresh, Message))
        {
            Message.SetLabels([.. fresh.Labels]);
            Message.SetAiPriority(fresh.AiPriority);
        }

        RefreshLabels();
        RefreshPriority();

        // The day may have changed since the item was shown.
        OnPropertyChanged(nameof(ListTimeText));
    }

    /// <summary>
    /// Gets the mark naming the account of the message, or <see langword="null"/> when the list needs none.
    /// </summary>
    [ObservableProperty]
    public partial AccountTagViewModel? AccountTag { get; private set; }

    /// <summary>
    /// Marks the row with an account, or removes the mark.
    /// </summary>
    /// <param name="account">The account of the message, or <see langword="null"/> for no mark.</param>
    public void ShowAccount(MailAccount? account)
    {
        if (account is null)
        {
            AccountTag = null;
            return;
        }

        var name = AddressText.AccountName(account);

        // An unchanged mark is kept, so a periodic refresh redraws nothing.
        var color = account.EffectiveColor;
        if (AccountTag?.Name != name || AccountTag.Address != account.EmailAddress || AccountTag.Color != color)
        {
            AccountTag = new AccountTagViewModel(name, account.EmailAddress, color);
        }
    }

    /// <summary>
    /// Builds the chips of the labels of a message.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <returns>The chips.</returns>
    private static LabelChipViewModel[] BuildChips(MailMessage message)
    {
        return [.. message.Labels.Select(l => new LabelChipViewModel(l.Name, l.Color))];
    }

    /// <summary>
    /// Gets or sets a value indicating whether the message is read.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnread))]
    public partial bool IsRead { get; set; }

    /// <summary>
    /// Gets a value indicating whether the message is unread.
    /// </summary>
    public bool IsUnread => !IsRead;

    /// <summary>
    /// Gets the short time shown in the message list.
    /// </summary>
    public string ListTimeText => MessageTimeFormatter.Format(Message.SentAt, _timeProvider, _localizer);

    /// <summary>
    /// Gets the initial shown in the sender's avatar.
    /// </summary>
    public string SenderInitial => Avatar.Initial(
        AddressText.Name(_contacts, Message.FromName, Message.FromAddress),
        Message.FromAddress);

    /// <summary>
    /// Gets the avatar color of the sender; created on first use so view models stay creatable off the UI thread.
    /// </summary>
    public IBrush SenderBrush => field ??= new SolidColorBrush(Color.Parse(Avatar.Color(Message.FromAddress)));

    /// <summary>
    /// Gets or sets a value indicating whether the message is flagged.
    /// </summary>
    [ObservableProperty]
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
        OnPropertyChanged(nameof(ListTimeText));
        RefreshPriority();
        RefreshContacts();
    }

    /// <summary>
    /// Re-reads the names and menu texts that come from the address book.
    /// </summary>
    public void RefreshContacts()
    {
        OnPropertyChanged(nameof(FromDisplay));
        OnPropertyChanged(nameof(SenderInitial));
        OnPropertyChanged(nameof(AlwaysAllowSenderText));
        _senderAddress?.Refresh();
        foreach (var recipient in _recipientAddresses ?? [])
        {
            recipient.Refresh();
        }
    }

    /// <summary>
    /// Gets the sender as shown in the list: <c>Name &lt;address&gt;</c> for a contact, otherwise the sender's display
    /// name, falling back to the address.
    /// </summary>
    public string FromDisplay => AddressText.Compact(_contacts, Message.FromName, Message.FromAddress);

    /// <summary>
    /// Gets the sender as shown in the reading pane; created on first use, because list rows do not need it.
    /// </summary>
    public MessageAddressViewModel SenderAddress =>
        _senderAddress ??= CreateAddress(Message.FromName, Message.FromAddress);

    /// <summary>
    /// Gets the To and Cc recipients as shown in the reading pane; created on first use.
    /// </summary>
    public IReadOnlyList<MessageAddressViewModel> RecipientAddresses =>
        _recipientAddresses ??=
        [
            .. RecipientList.Split(Message.Recipients)
                .Select(RecipientList.Parse)
                .Select(r => CreateAddress(r.Name, r.Address)),
        ];

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
    /// Gets the complete HTML document for the embedded web view, or <see langword="null"/> when the message has no HTML body.
    /// </summary>
    public string? HtmlDocument => HasHtml ? HtmlMailDocument.Build(Message.BodyHtml!, AllowRemoteImages, InlineImages) : null;

    /// <summary>
    /// Gets the images embedded in the message that were downloaded so far, keyed by normalized content identifier.
    /// </summary>
    public IReadOnlyDictionary<string, InlineImage> InlineImages { get; private set; } = new Dictionary<string, InlineImage>();

    /// <summary>
    /// Gets a value indicating whether the HTML body refers to images embedded in the message (<c>cid:</c>).
    /// </summary>
    public bool HasInlineImageReferences => InlineImageReferences.Find(Message.BodyHtml).Count > 0;

    /// <summary>
    /// Gets or sets a value indicating whether the embedded images were already requested, so they are fetched only once.
    /// </summary>
    public bool InlineImagesRequested { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the user allowed this message to load images from the web.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HtmlDocument))]
    [NotifyPropertyChangedFor(nameof(HasBlockedRemoteImages))]
    public partial bool AllowRemoteImages { get; set; }

    /// <summary>
    /// Gets a value indicating whether the message refers to images on the web that are currently blocked.
    /// </summary>
    public bool HasBlockedRemoteImages => !AllowRemoteImages && HtmlMailDocument.HasRemoteImages(Message.BodyHtml);

    /// <summary>
    /// Stores downloaded embedded images and refreshes the HTML document.
    /// </summary>
    /// <param name="images">The downloaded images.</param>
    public void SetInlineImages(IReadOnlyDictionary<string, InlineImage> images)
    {
        ArgumentNullException.ThrowIfNull(images);
        InlineImages = images;
        OnPropertyChanged(nameof(HtmlDocument));
    }

    /// <summary>
    /// Gets the rule that allows images for this sender's address, or <see langword="null"/> when the sender address is unusable.
    /// </summary>
    public string? SenderRule => RemoteImageRule.Candidates(Message.FromAddress) is [var address, ..] ? address : null;

    /// <summary>
    /// Gets the rule that allows images for every sender at this sender's domain, or <see langword="null"/> when the sender address is unusable.
    /// </summary>
    public string? DomainRule => RemoteImageRule.Candidates(Message.FromAddress) is [_, var domain, ..] ? domain : null;

    /// <summary>
    /// Gets a value indicating whether a rule for this sender can be saved.
    /// </summary>
    public bool CanRememberSender => SenderRule is not null && _rememberRemoteImages is not null;

    /// <summary>
    /// Gets the menu text of the "always allow this sender" choice.
    /// </summary>
    public string AlwaysAllowSenderText => _localizer.Format(
        "Reader_AlwaysAllowSender",
        SenderRule is { } rule ? AddressText.Compact(_contacts, null, rule) : string.Empty);

    /// <summary>
    /// Gets the menu text of the "always allow this sender's domain" choice.
    /// </summary>
    public string AlwaysAllowDomainText => _localizer.Format("Reader_AlwaysAllowDomain", DomainRule ?? string.Empty);

    /// <summary>
    /// Creates the header entry of one address of this message.
    /// </summary>
    /// <param name="name">The name that came with the mail.</param>
    /// <param name="address">The address.</param>
    /// <returns>The entry.</returns>
    private MessageAddressViewModel CreateAddress(string name, string address)
    {
        return new MessageAddressViewModel(Message.AccountId, name, address, _contacts, _localizer, _addressActions);
    }

    /// <summary>
    /// Allows this message to load images from the web.
    /// </summary>
    [RelayCommand]
    private void LoadRemoteImages()
    {
        AllowRemoteImages = true;
    }

    /// <summary>
    /// Allows images in this message and saves a rule that always allows them for this sender's address.
    /// </summary>
    /// <returns>A task that completes when the rule is saved.</returns>
    [RelayCommand]
    private Task AlwaysAllowSenderAsync()
    {
        return RememberAsync(SenderRule);
    }

    /// <summary>
    /// Allows images in this message and saves a rule that always allows them for this sender's domain.
    /// </summary>
    /// <returns>A task that completes when the rule is saved.</returns>
    [RelayCommand]
    private Task AlwaysAllowDomainAsync()
    {
        return RememberAsync(DomainRule);
    }

    /// <summary>
    /// Allows images in this message right away, then saves the rule.
    /// </summary>
    /// <param name="rule">The rule to save, or <see langword="null"/> when there is none.</param>
    /// <returns>A task that completes when the rule is saved.</returns>
    private async Task RememberAsync(string? rule)
    {
        AllowRemoteImages = true;
        if (rule is not null && _rememberRemoteImages is not null)
        {
            await _rememberRemoteImages(this, rule);
        }
    }

    /// <summary>
    /// Gets a value indicating whether the message has an HTML body.
    /// </summary>
    public bool HasHtml => !string.IsNullOrWhiteSpace(Message.BodyHtml);

    /// <summary>
    /// Gets a value indicating whether the message has attachments.
    /// </summary>
    public bool HasAttachments => Attachments.Count > 0;
}
