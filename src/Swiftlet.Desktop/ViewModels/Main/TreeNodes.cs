using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Swiftlet.Desktop.Formatting;
using Swiftlet.Desktop.Localization;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Labels;

namespace Swiftlet.Desktop.ViewModels.Main;

/// <summary>
/// An account node in the folder tree.
/// </summary>
/// <param name="account">The account.</param>
public sealed partial class AccountNodeViewModel(MailAccount account) : ObservableObject
{
    /// <summary>
    /// Gets or sets the number of unread messages downloaded into the account's inbox.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnreadText))]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    public partial int UnreadCount { get; set; }

    /// <summary>
    /// Gets the badge text.
    /// </summary>
    public string UnreadText => UnreadBadge.Format(UnreadCount);

    /// <summary>
    /// Gets a value indicating whether the badge is shown.
    /// </summary>
    public bool HasUnread => UnreadCount > 0;

    /// <summary>
    /// Gets the account.
    /// </summary>
    public MailAccount Account { get; private set; } = account;

    /// <summary>
    /// Gets the text shown in the tree: the name the user gave the account, otherwise its address.
    /// </summary>
    public string DisplayName => Account.HasCustomDisplayName ? Account.DisplayName : Account.EmailAddress;

    /// <summary>
    /// Gets the initial shown in the account's avatar; taken from the text the tree shows.
    /// </summary>
    public string Initial => Avatar.Initial(DisplayName, Account.EmailAddress);

    /// <summary>
    /// Gets the avatar color; created on first use so view models stay creatable off the UI thread.
    /// </summary>
    public IBrush AvatarBrush => field ??= new SolidColorBrush(Color.Parse(Avatar.Color(Account.EmailAddress)));

    /// <summary>
    /// Gets a value indicating whether the account signs in with OAuth 2.0 and can therefore be signed in again.
    /// </summary>
    public bool CanReauthenticate => Account.AuthMethod == AuthMethod.OAuth2;

    /// <summary>
    /// Gets the folders of the account.
    /// </summary>
    public ObservableCollection<FolderNodeViewModel> Folders { get; } = [];

    /// <summary>
    /// Replaces the account with a newer read of the same account, so changed settings show in the tree.
    /// </summary>
    /// <param name="updated">The account as currently stored.</param>
    public void Update(MailAccount updated)
    {
        ArgumentNullException.ThrowIfNull(updated);
        Account = updated;
        OnPropertyChanged(nameof(Account));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Initial));
    }
}

/// <summary>
/// A folder node in the folder tree.
/// </summary>
/// <param name="folder">The folder.</param>
/// <param name="account">The owning account.</param>
/// <param name="localizer">The localizer used for well-known folder names.</param>
public sealed partial class FolderNodeViewModel(MailFolder folder, MailAccount account, ILocalizer localizer) : ObservableObject
{
    /// <summary>
    /// Gets the folder.
    /// </summary>
    public MailFolder Folder { get; } = folder;

    /// <summary>
    /// Gets or sets the number of unread messages downloaded into the folder.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnreadText))]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    public partial int UnreadCount { get; set; }

    /// <summary>
    /// Gets the badge text.
    /// </summary>
    public string UnreadText => UnreadBadge.Format(UnreadCount);

    /// <summary>
    /// Gets a value indicating whether the badge is shown.
    /// </summary>
    public bool HasUnread => UnreadCount > 0;

    /// <summary>
    /// Gets the owning account.
    /// </summary>
    public MailAccount Account { get; } = account;

    /// <summary>
    /// Gets a value indicating whether all messages of the folder can be deleted at once. All Mail cannot: it mirrors every
    /// other folder, so emptying it would delete the whole mailbox.
    /// </summary>
    public bool CanEmpty => Folder.Role != FolderRole.AllMail;

    /// <summary>
    /// Gets the text shown in the tree; well-known folders use localized names.
    /// </summary>
    public string Name => Folder.Role switch
    {
        FolderRole.Inbox => localizer["Folder_Inbox"],
        FolderRole.Sent => localizer["Folder_Sent"],
        FolderRole.Drafts => localizer["Folder_Drafts"],
        FolderRole.Trash => localizer["Folder_Trash"],
        FolderRole.Junk => localizer["Folder_Junk"],
        FolderRole.Archive => localizer["Folder_Archive"],
        FolderRole.AllMail => localizer["Folder_AllMail"],
        _ => Folder.Name,
    };

    /// <summary>
    /// Gets the resource key of the folder's icon.
    /// </summary>
    public string IconKey => Folder.Role switch
    {
        FolderRole.Inbox => "Icon.Inbox",
        FolderRole.Sent => "Icon.Sent",
        FolderRole.Drafts => "Icon.Drafts",
        FolderRole.Trash => "Icon.Trash",
        FolderRole.Junk => "Icon.Junk",
        FolderRole.Archive => "Icon.Archive",
        FolderRole.AllMail => "Icon.AllMail",
        _ => "Icon.Folder",
    };

    /// <summary>
    /// Gets the sort rank: well-known folders first, then the rest alphabetically.
    /// </summary>
    public int SortRank => Folder.Role switch
    {
        FolderRole.Inbox => 0,
        FolderRole.Drafts => 1,
        FolderRole.Sent => 2,
        FolderRole.Archive => 3,
        FolderRole.AllMail => 3,
        FolderRole.Junk => 4,
        FolderRole.Trash => 5,
        _ => 6,
    };

    /// <summary>
    /// Re-reads the name after the UI language changed.
    /// </summary>
    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(Name));
    }
}

/// <summary>
/// The tree node listing the inboxes of all accounts together.
/// </summary>
/// <param name="localizer">The localizer used for the node name.</param>
public sealed partial class UnifiedInboxNodeViewModel(ILocalizer localizer) : ObservableObject
{
    /// <summary>
    /// Gets or sets the number of unread messages downloaded into all inboxes.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UnreadText))]
    [NotifyPropertyChangedFor(nameof(HasUnread))]
    public partial int UnreadCount { get; set; }

    /// <summary>
    /// Gets the badge text.
    /// </summary>
    public string UnreadText => UnreadBadge.Format(UnreadCount);

    /// <summary>
    /// Gets a value indicating whether the badge is shown.
    /// </summary>
    public bool HasUnread => UnreadCount > 0;

    /// <summary>
    /// Gets the text shown in the tree.
    /// </summary>
    public string Name => localizer["Folder_UnifiedInbox"];

    /// <summary>
    /// Re-reads the name after the UI language changed.
    /// </summary>
    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(Name));
    }
}

/// <summary>
/// A label node in the tree; selecting it lists the messages carrying the label.
/// </summary>
/// <param name="label">The label.</param>
/// <param name="startsSection">Whether this is the first label node, which is set apart from the accounts above.</param>
public sealed class LabelNodeViewModel(Label label, bool startsSection = false)
{
    /// <summary>
    /// Gets a value indicating whether this is the first label node, which is set apart from the accounts above.
    /// </summary>
    public bool StartsSection { get; } = startsSection;

    /// <summary>
    /// Gets the label.
    /// </summary>
    public Label Label { get; } = label;

    /// <summary>
    /// Gets the label name.
    /// </summary>
    public string Name => Label.Name;

    /// <summary>
    /// Gets the label color as <c>#RRGGBB</c>.
    /// </summary>
    public string Color => Label.Color;

    /// <summary>
    /// Gets the brush used to paint the label color; created on first use so view models stay creatable off the UI thread.
    /// </summary>
    public IBrush Brush => field ??= new SolidColorBrush(Avalonia.Media.Color.Parse(Label.Color));
}

/// <summary>
/// A colored label chip shown on a message list item.
/// </summary>
/// <param name="Name">The label name.</param>
/// <param name="Color">The label color as <c>#RRGGBB</c>.</param>
public sealed record LabelChipViewModel(string Name, string Color)
{
    /// <summary>
    /// Gets the brush used to paint the chip; created on first use so view models stay creatable off the UI thread.
    /// </summary>
    public IBrush Brush => field ??= new SolidColorBrush(Avalonia.Media.Color.Parse(Color));

    /// <summary>
    /// Gets the light background of the chip: the label color mostly see-through, so the text stays readable on any color.
    /// </summary>
    public IBrush TintBrush => field ??= Tint(Avalonia.Media.Color.Parse(Color));

    /// <summary>
    /// Creates the see-through brush of a color.
    /// </summary>
    /// <param name="color">The color.</param>
    /// <returns>The brush.</returns>
    private static SolidColorBrush Tint(Avalonia.Media.Color color)
    {
        return new SolidColorBrush(Avalonia.Media.Color.FromArgb(0x26, color.R, color.G, color.B));
    }
}
