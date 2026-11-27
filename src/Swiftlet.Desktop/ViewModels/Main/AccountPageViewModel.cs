using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Swiftlet.Application.Messages;
using Swiftlet.Application.Sync;
using Swiftlet.Desktop.Formatting;
using Swiftlet.Desktop.Localization;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Desktop.ViewModels.Main;

/// <summary>
/// The downloaded messages of one folder as listed on the account page.
/// </summary>
/// <param name="Name">The folder name.</param>
/// <param name="Total">The number of messages.</param>
/// <param name="Unread">The number of unread messages.</param>
public sealed record AccountFolderRow(string Name, string Total, string Unread);

/// <summary>
/// The information page of the account selected in the tree. Every text is derived from the account node, the message
/// counts and the sync status, so <see cref="Refresh"/> updates the whole page.
/// </summary>
/// <param name="node">The account node.</param>
/// <param name="localizer">The localizer.</param>
/// <param name="syncCommand">Synchronizes the selected account.</param>
/// <param name="editCommand">Opens the account settings; takes the node.</param>
/// <param name="reauthenticateCommand">Signs the account in again; takes the node.</param>
/// <param name="deleteCommand">Removes the account; takes the node.</param>
public sealed class AccountPageViewModel(
    AccountNodeViewModel node,
    ILocalizer localizer,
    ICommand syncCommand,
    ICommand editCommand,
    ICommand reauthenticateCommand,
    ICommand deleteCommand) : ObservableObject
{
    /// <summary>
    /// Shown for a value that is not known yet.
    /// </summary>
    private const string Unknown = "—";

    /// <summary>
    /// The message counts per folder, or <see langword="null"/> while they are not loaded.
    /// </summary>
    private IReadOnlyDictionary<Guid, FolderMessageCount>? _counts;

    /// <summary>
    /// How the synchronization of the account has gone.
    /// </summary>
    private AccountSyncStatus _status = AccountSyncStatus.Unknown;

    /// <summary>
    /// Gets the account node.
    /// </summary>
    public AccountNodeViewModel Node { get; } = node;

    /// <summary>
    /// Gets the command synchronizing the account.
    /// </summary>
    public ICommand SyncCommand { get; } = syncCommand;

    /// <summary>
    /// Gets the command opening the account settings.
    /// </summary>
    public ICommand EditCommand { get; } = editCommand;

    /// <summary>
    /// Gets the command signing the account in again.
    /// </summary>
    public ICommand ReauthenticateCommand { get; } = reauthenticateCommand;

    /// <summary>
    /// Gets the command removing the account.
    /// </summary>
    public ICommand DeleteCommand { get; } = deleteCommand;

    /// <summary>
    /// Gets the one-line summary of the synchronization.
    /// </summary>
    public string SyncSummary
    {
        get
        {
            if (_status.IsSyncing)
            {
                return localizer["AccountPage_Syncing"];
            }

            if (_status.LastFailedAt is { } failed)
            {
                return localizer.Format("AccountPage_SyncFailed", FormatTime(failed));
            }

            return _status.LastSucceededAt is { } succeeded
                ? localizer.Format("AccountPage_SyncSucceeded", FormatTime(succeeded))
                : localizer["AccountPage_NotSynced"];
        }
    }

    /// <summary>
    /// Gets why the last sync failed, or what to do about it; empty if it did not fail.
    /// </summary>
    public string SyncError => _status.RequiresSignIn
        ? localizer["AccountPage_SignInRequired"]
        : _status.Error ?? string.Empty;

    /// <summary>
    /// Gets a value indicating whether there is an error to show.
    /// </summary>
    public bool HasSyncError => SyncError.Length > 0;

    /// <summary>
    /// Gets when the account was last synchronized successfully, shown while a later sync runs or after one failed;
    /// otherwise empty.
    /// </summary>
    public string LastSuccessText =>
        (_status.IsSyncing || _status.LastFailedAt is not null) && _status.LastSucceededAt is { } succeeded
            ? localizer.Format("AccountPage_SyncSucceeded", FormatTime(succeeded))
            : string.Empty;

    /// <summary>
    /// Gets a value indicating whether the earlier success is shown.
    /// </summary>
    public bool HasLastSuccess => LastSuccessText.Length > 0;

    /// <summary>
    /// Gets the folders the last successful sync could not synchronize; empty if there are none.
    /// </summary>
    public string FailedFoldersText => _status.FailedFolders.Count > 0
        ? localizer.Format("AccountPage_FailedFolders", string.Join(", ", _status.FailedFolders))
        : string.Empty;

    /// <summary>
    /// Gets a value indicating whether some folders failed.
    /// </summary>
    public bool HasFailedFolders => FailedFoldersText.Length > 0;

    /// <summary>
    /// Gets the number of unread messages downloaded into the inbox.
    /// </summary>
    public string InboxUnreadText => _counts is null
        ? Unknown
        : Format(Node.Folders.Where(f => f.Folder.Role == Domain.Folders.FolderRole.Inbox).Sum(f => Count(f).Unread));

    /// <summary>
    /// Gets the number of downloaded messages, adding up all folders.
    /// </summary>
    public string MessageCountText => _counts is null ? Unknown : Format(Node.Folders.Sum(f => Count(f).Total));

    /// <summary>
    /// Gets the number of folders.
    /// </summary>
    public string FolderCountText => Format(Node.Folders.Count);

    /// <summary>
    /// Gets the folders with their message counts, in the order of the tree.
    /// </summary>
    public IReadOnlyList<AccountFolderRow> FolderRows =>
    [
        .. Node.Folders.Select(f => new AccountFolderRow(
            f.Name,
            _counts is null ? Unknown : Format(Count(f).Total),
            _counts is null ? Unknown : Format(Count(f).Unread))),
    ];

    /// <summary>
    /// Gets how the account signs in.
    /// </summary>
    public string SignInMethodText => Node.Account.AuthMethod == AuthMethod.OAuth2
        ? localizer.Format("AccountPage_SignInBrowser", Node.Account.OAuthProvider?.ToString() ?? string.Empty)
        : localizer["AccountPage_SignInPassword"];

    /// <summary>
    /// Gets the sender name of outgoing mail.
    /// </summary>
    public string SenderName => Node.Account.DisplayName;

    /// <summary>
    /// Gets the synchronization interval.
    /// </summary>
    public string SyncIntervalDescription => SyncIntervalText.Format(localizer, Node.Account.SyncInterval);

    /// <summary>
    /// Gets the folder archived messages are moved to.
    /// </summary>
    public string ArchiveFolderText
    {
        get
        {
            var folders = Node.Folders.Select(f => f.Folder).ToList();
            var resolved = ArchiveFolderResolver.Resolve(Node.Account, folders);
            if (Node.Account.ArchiveFolderName is { } chosen)
            {
                return resolved is null ? localizer.Format("EditAccount_ArchiveMissing", chosen) : chosen;
            }

            return resolved is null
                ? localizer["EditAccount_ArchiveAutomaticNone"]
                : localizer.Format("EditAccount_ArchiveAutomatic", resolved.FullName);
        }
    }

    /// <summary>
    /// Gets when the account was added.
    /// </summary>
    public string AddedAtText => FormatTime(Node.Account.CreatedAt);

    /// <summary>
    /// Gets the address of the IMAP server.
    /// </summary>
    public string ImapAddress => FormatAddress(Node.Account.Imap);

    /// <summary>
    /// Gets the encryption of the IMAP connection.
    /// </summary>
    public string ImapSecurity => FormatSecurity(Node.Account.Imap);

    /// <summary>
    /// Gets the address of the SMTP server.
    /// </summary>
    public string SmtpAddress => FormatAddress(Node.Account.Smtp);

    /// <summary>
    /// Gets the encryption of the SMTP connection.
    /// </summary>
    public string SmtpSecurity => FormatSecurity(Node.Account.Smtp);

    /// <summary>
    /// Shows newly read message counts.
    /// </summary>
    /// <param name="counts">The counts of the folders that have messages.</param>
    public void SetCounts(IReadOnlyList<FolderMessageCount> counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        _counts = counts.ToDictionary(c => c.FolderId);
        Refresh();
    }

    /// <summary>
    /// Shows a newly read synchronization status.
    /// </summary>
    /// <param name="status">The status.</param>
    public void SetStatus(AccountSyncStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        _status = status;
        Refresh();
    }

    /// <summary>
    /// Re-reads every text, after the account, its folders or the UI language changed.
    /// </summary>
    public void Refresh()
    {
        // An empty name tells the bindings that every property may have changed.
        OnPropertyChanged(string.Empty);
    }

    /// <summary>
    /// Formats a point in time in local time.
    /// </summary>
    /// <param name="time">The time.</param>
    /// <returns>The text.</returns>
    private static string FormatTime(DateTimeOffset time)
    {
        return time.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Formats a count.
    /// </summary>
    /// <param name="count">The count.</param>
    /// <returns>The text.</returns>
    private static string Format(int count)
    {
        return count.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Formats a server address.
    /// </summary>
    /// <param name="endpoint">The server.</param>
    /// <returns>The host and port.</returns>
    private static string FormatAddress(ServerEndpoint endpoint)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{endpoint.Host}:{endpoint.Port}");
    }

    /// <summary>
    /// Names the encryption of a server connection.
    /// </summary>
    /// <param name="endpoint">The server.</param>
    /// <returns>The protocol name, which is the same in every language.</returns>
    private static string FormatSecurity(ServerEndpoint endpoint)
    {
        return endpoint.UseSsl ? "SSL/TLS" : "STARTTLS";
    }

    /// <summary>
    /// Gets the counts of a folder.
    /// </summary>
    /// <param name="folder">The folder node.</param>
    /// <returns>The counts; zero for a folder without cached messages.</returns>
    private FolderMessageCount Count(FolderNodeViewModel folder)
    {
        return _counts?.GetValueOrDefault(folder.Folder.Id) ?? new FolderMessageCount(folder.Folder.Id, 0, 0);
    }
}
