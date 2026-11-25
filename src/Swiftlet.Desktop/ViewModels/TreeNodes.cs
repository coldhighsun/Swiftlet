using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Swiftlet.Desktop.Localization;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;

namespace Swiftlet.Desktop.ViewModels;

/// <summary>
/// An account node in the folder tree.
/// </summary>
/// <param name="account">The account.</param>
public sealed class AccountNodeViewModel(MailAccount account)
{
    /// <summary>
    /// Gets the account.
    /// </summary>
    public MailAccount Account { get; } = account;

    /// <summary>
    /// Gets the text shown in the tree.
    /// </summary>
    public string DisplayName => Account.EmailAddress;

    /// <summary>
    /// Gets the folders of the account.
    /// </summary>
    public ObservableCollection<FolderNodeViewModel> Folders { get; } = [];
}

/// <summary>
/// A folder node in the folder tree.
/// </summary>
/// <param name="folder">The folder.</param>
/// <param name="account">The owning account.</param>
/// <param name="localizer">The localizer used for well-known folder names.</param>
public sealed class FolderNodeViewModel(MailFolder folder, MailAccount account, ILocalizer localizer) : ObservableObject
{
    /// <summary>
    /// Gets the folder.
    /// </summary>
    public MailFolder Folder { get; } = folder;

    /// <summary>
    /// Gets the owning account.
    /// </summary>
    public MailAccount Account { get; } = account;

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
        _ => Folder.Name,
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
