using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Swiftlet.Application.Messages;
using Swiftlet.Application.Queries;
using Swiftlet.Application.Sync;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Services;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Desktop.ViewModels;

/// <summary>
/// The view model of the main three-pane window: folder tree, message list and reading pane.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    /// <summary>
    /// The maximum number of messages shown per folder or search.
    /// </summary>
    private const int PageSize = 200;

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
    /// The accounts last loaded, used to populate compose windows.
    /// </summary>
    private IReadOnlyList<MailAccount> _accounts = [];

    /// <summary>
    /// Incremented for every message list load so slower, outdated loads can be discarded.
    /// </summary>
    private int _loadVersion;

    /// <summary>
    /// The most recent message list load.
    /// </summary>
    private Task _loadTask = Task.CompletedTask;

    /// <summary>
    /// The most recent selection side effect (marking as read).
    /// </summary>
    private Task _selectionTask = Task.CompletedTask;

    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindowViewModel"/> class.
    /// </summary>
    /// <param name="dispatcher">The request dispatcher.</param>
    /// <param name="dialogs">The dialog service.</param>
    /// <param name="localizer">The localizer.</param>
    /// <param name="languageSelector">The language picker view model.</param>
    public MainWindowViewModel(
        IRequestDispatcher dispatcher,
        IDialogService dialogs,
        ILocalizer localizer,
        LanguageSelectorViewModel languageSelector)
    {
        _dispatcher = dispatcher;
        _dialogs = dialogs;
        _localizer = localizer;
        LanguageSelector = languageSelector;
        SearchText = string.Empty;
        StatusText = string.Empty;
        localizer.LanguageChanged += (_, _) => RefreshLanguage();
    }

    /// <summary>
    /// Gets the language picker.
    /// </summary>
    public LanguageSelectorViewModel LanguageSelector { get; }

    /// <summary>
    /// Gets the account nodes of the folder tree.
    /// </summary>
    public ObservableCollection<AccountNodeViewModel> Accounts { get; } = [];

    /// <summary>
    /// Gets the messages of the selected folder or search.
    /// </summary>
    public ObservableCollection<MessageItemViewModel> Messages { get; } = [];

    /// <summary>
    /// Gets or sets the selected tree node: an <see cref="AccountNodeViewModel"/> or a <see cref="FolderNodeViewModel"/>.
    /// </summary>
    [ObservableProperty]
    public partial object? SelectedNode { get; set; }

    /// <summary>
    /// Gets or sets the selected message.
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ReplyCommand))]
    [NotifyCanExecuteChangedFor(nameof(ForwardCommand))]
    [NotifyCanExecuteChangedFor(nameof(ToggleFlagCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenHtmlCommand))]
    [NotifyPropertyChangedFor(nameof(IsNoMessageSelected))]
    public partial MessageItemViewModel? SelectedMessage { get; set; }

    /// <summary>
    /// Gets a value indicating whether no message is selected, so the reading pane shows its placeholder.
    /// </summary>
    public bool IsNoMessageSelected => SelectedMessage is null;

    /// <summary>
    /// Gets or sets the search text.
    /// </summary>
    [ObservableProperty]
    public partial string SearchText { get; set; }

    /// <summary>
    /// Gets or sets the status line text.
    /// </summary>
    [ObservableProperty]
    public partial string StatusText { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a long-running operation is in progress.
    /// </summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>
    /// Loads the accounts and shows the add-account dialog when none are configured.
    /// </summary>
    /// <returns>A task that completes when initialization is done.</returns>
    public async Task InitializeAsync()
    {
        await GuardedAsync("Error_LoadAccounts", ReloadAccountsAsync);
        if (Accounts.Count == 0)
        {
            await AddAccountAsync();
        }
    }

    /// <summary>
    /// Waits until pending list loads and selection side effects have finished.
    /// </summary>
    /// <returns>A task that completes when the view model is idle.</returns>
    internal async Task WhenIdleAsync()
    {
        await _loadTask;
        await _selectionTask;
    }

    /// <summary>
    /// Opens the add-account dialog and synchronizes the new account.
    /// </summary>
    /// <returns>A task that completes when the dialog and the first sync are done.</returns>
    [RelayCommand]
    private async Task AddAccountAsync()
    {
        var known = _accounts.Select(a => a.Id).ToHashSet();
        if (!await _dialogs.ShowAddAccountAsync())
        {
            return;
        }

        await GuardedAsync("Error_LoadAccounts", ReloadAccountsAsync);
        var added = _accounts.Where(a => !known.Contains(a.Id)).ToList();
        await SyncAccountsAsync(added);
    }

    /// <summary>
    /// Synchronizes the selected account, or all accounts when nothing is selected.
    /// </summary>
    /// <returns>A task that completes when the sync is done.</returns>
    [RelayCommand]
    private async Task SyncAsync()
    {
        var scope = ScopeAccountId(SelectedNode);
        var targets = scope is { } id ? _accounts.Where(a => a.Id == id).ToList() : _accounts.ToList();
        await SyncAccountsAsync(targets);
    }

    /// <summary>
    /// Reloads folders and the current message list without synchronizing with the server.
    /// </summary>
    /// <returns>A task that completes when the refresh is done.</returns>
    [RelayCommand]
    public async Task RefreshAsync()
    {
        await GuardedAsync("Error_Refresh", ReloadAccountsAsync);
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            _loadTask = LoadMessagesAsync(SelectedNode);
            await _loadTask;
        }
    }

    /// <summary>
    /// Searches the cached messages within the selected account (or all accounts), or restores the folder view when blank.
    /// </summary>
    /// <returns>A task that completes when the search results are shown.</returns>
    [RelayCommand]
    private async Task SearchAsync()
    {
        var text = SearchText.Trim();
        if (text.Length == 0)
        {
            _loadTask = LoadMessagesAsync(SelectedNode);
            await _loadTask;
            return;
        }

        var version = ++_loadVersion;
        await GuardedAsync("Error_Search", async () =>
        {
            var results = await _dispatcher.SendAsync(new SearchMessagesQuery(text, ScopeAccountId(SelectedNode), PageSize));
            if (version != _loadVersion)
            {
                return;
            }

            ReplaceMessages(results);
            StatusText = _localizer.Format("Status_SearchResults", results.Count);
        });
    }

    /// <summary>
    /// Opens a compose window for a new message.
    /// </summary>
    /// <returns>A task that completes when the window has been opened.</returns>
    [RelayCommand]
    private async Task ComposeAsync()
    {
        var accountId = ScopeAccountId(SelectedNode) ?? (_accounts.Count > 0 ? _accounts[0].Id : null);
        await _dialogs.ShowComposeAsync(_accounts, new ComposeRequest(ComposeMode.New, accountId, string.Empty, string.Empty, string.Empty, null));
    }

    /// <summary>
    /// Opens a compose window replying to the selected message.
    /// </summary>
    /// <returns>A task that completes when the window has been opened.</returns>
    [RelayCommand(CanExecute = nameof(HasSelectedMessage))]
    private async Task ReplyAsync()
    {
        await _dialogs.ShowComposeAsync(_accounts, ComposeTemplates.Reply(SelectedMessage!.Message, _localizer));
    }

    /// <summary>
    /// Opens a compose window forwarding the selected message.
    /// </summary>
    /// <returns>A task that completes when the window has been opened.</returns>
    [RelayCommand(CanExecute = nameof(HasSelectedMessage))]
    private async Task ForwardAsync()
    {
        await _dialogs.ShowComposeAsync(_accounts, ComposeTemplates.Forward(SelectedMessage!.Message, _localizer));
    }

    /// <summary>
    /// Toggles the flagged (starred) state of the selected message.
    /// </summary>
    /// <returns>A task that completes when the state has been saved.</returns>
    [RelayCommand(CanExecute = nameof(HasSelectedMessage))]
    private async Task ToggleFlagAsync()
    {
        var item = SelectedMessage!;
        var flagged = !item.IsFlagged;
        item.IsFlagged = flagged;
        try
        {
            await _dispatcher.SendAsync(new SetMessageFlaggedCommand(item.Id, flagged));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Roll back the optimistic update and tell the user; a failed star must not crash the UI.
            item.IsFlagged = !flagged;
            StatusText = _localizer.Format("Error_Format", _localizer["Error_Flag"], ex.Message);
        }
    }

    /// <summary>
    /// Opens the HTML body of the selected message in the system browser.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenHtml))]
    private void OpenHtml()
    {
        _dialogs.ShowHtmlInBrowser(SelectedMessage!.Message.BodyHtml!);
    }

    /// <summary>
    /// Gets a value indicating whether a message is selected.
    /// </summary>
    private bool HasSelectedMessage => SelectedMessage is not null;

    /// <summary>
    /// Gets a value indicating whether the selected message has an HTML body.
    /// </summary>
    private bool CanOpenHtml => SelectedMessage is { HasHtml: true };

    /// <summary>
    /// Loads the messages of a newly selected folder.
    /// </summary>
    /// <param name="value">The new selection.</param>
    partial void OnSelectedNodeChanged(object? value)
    {
        SearchText = string.Empty;
        _loadTask = LoadMessagesAsync(value);
    }

    /// <summary>
    /// Refreshes folder names and placeholder subjects after the UI language changed.
    /// </summary>
    private void RefreshLanguage()
    {
        foreach (var folder in Accounts.SelectMany(a => a.Folders))
        {
            folder.RefreshLanguage();
        }

        foreach (var message in Messages)
        {
            message.RefreshLanguage();
        }
    }

    /// <summary>
    /// Marks a newly selected unread message as read.
    /// </summary>
    /// <param name="value">The new selection.</param>
    partial void OnSelectedMessageChanged(MessageItemViewModel? value)
    {
        if (value is { IsRead: false })
        {
            _selectionTask = MarkReadAsync(value);
        }
    }

    /// <summary>
    /// Marks a message read locally and persists the state.
    /// </summary>
    /// <param name="item">The message.</param>
    /// <returns>A task that completes when the state has been saved.</returns>
    private async Task MarkReadAsync(MessageItemViewModel item)
    {
        item.IsRead = true;
        try
        {
            await _dispatcher.SendAsync(new SetMessageReadCommand(item.Id, true));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Roll back the optimistic update; the message simply stays unread.
            item.IsRead = false;
            StatusText = _localizer.Format("Error_Format", _localizer["Error_MarkRead"], ex.Message);
        }
    }

    /// <summary>
    /// Saves an attachment of the selected message to a user-chosen path.
    /// </summary>
    /// <param name="attachment">The attachment.</param>
    /// <returns>A task that completes when the file has been written.</returns>
    private async Task SaveAttachmentAsync(AttachmentItemViewModel attachment)
    {
        var path = await _dialogs.PickSavePathAsync(attachment.Attachment.FileName);
        if (path is null)
        {
            return;
        }

        await GuardedAsync("Error_SaveAttachment", async () =>
        {
            StatusText = _localizer.Format("Status_Downloading", attachment.Attachment.FileName);
            var downloaded = await _dispatcher.SendAsync(
                new DownloadAttachmentQuery(attachment.Attachment.MessageId, attachment.Attachment.Id));
            await File.WriteAllBytesAsync(path, downloaded.Content);
            StatusText = _localizer.Format("Status_Saved", path);
        });
    }

    /// <summary>
    /// Synchronizes the given accounts one after another and reloads the view.
    /// </summary>
    /// <param name="targets">The accounts to synchronize.</param>
    /// <returns>A task that completes when all accounts have been attempted.</returns>
    private async Task SyncAccountsAsync(List<MailAccount> targets)
    {
        if (targets.Count == 0)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var newMessages = 0;
            var problems = new List<string>();
            foreach (var account in targets)
            {
                StatusText = _localizer.Format("Status_Syncing", account.EmailAddress);
                try
                {
                    var result = await _dispatcher.SendAsync(new SyncAccountCommand(account.Id));
                    newMessages += result.NewMessages;
                    if (result.FailedFolders.Count > 0)
                    {
                        problems.Add(_localizer.Format("Status_AccountFoldersFailed", account.EmailAddress, result.FailedFolders.Count));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One unreachable account must not prevent the others from synchronizing.
                    problems.Add($"{account.EmailAddress}: {ex.Message}");
                }
            }

            await GuardedAsync("Error_Refresh", ReloadAccountsAsync);
            _loadTask = LoadMessagesAsync(SelectedNode);
            await _loadTask;
            StatusText = problems.Count == 0
                ? _localizer.Format("Status_SyncDone", newMessages)
                : _localizer.Format("Status_SyncDoneWithProblems", newMessages, string.Join(_localizer["Status_ProblemSeparator"], problems));
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Merges the accounts and folders from the database into the tree without disturbing unchanged nodes.
    /// </summary>
    /// <returns>A task that completes when the tree is up to date.</returns>
    private async Task ReloadAccountsAsync()
    {
        _accounts = await _dispatcher.SendAsync(new GetAccountsQuery());

        foreach (var account in _accounts)
        {
            var node = Accounts.FirstOrDefault(n => n.Account.Id == account.Id);
            if (node is null)
            {
                node = new AccountNodeViewModel(account);
                Accounts.Add(node);
            }

            var folders = await _dispatcher.SendAsync(new GetFoldersQuery(account.Id));
            var current = node.Folders.Select(f => f.Folder.Id).ToHashSet();
            if (current.SetEquals(folders.Select(f => f.Id)))
            {
                continue;
            }

            var selectedFolderId = (SelectedNode as FolderNodeViewModel)?.Folder.Id;
            var nodes = folders
                .Select(f => new FolderNodeViewModel(f, account, _localizer))
                .OrderBy(f => f.SortRank)
                .ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            node.Folders.Clear();
            foreach (var folderNode in nodes)
            {
                node.Folders.Add(folderNode);
            }

            if (selectedFolderId is { } id)
            {
                SelectedNode = nodes.FirstOrDefault(f => f.Folder.Id == id);
            }
        }

        if (SelectedNode is null)
        {
            SelectedNode = Accounts
                .SelectMany(a => a.Folders)
                .OrderBy(f => f.SortRank)
                .FirstOrDefault();
        }
    }

    /// <summary>
    /// Loads the messages for a tree selection and shows them.
    /// </summary>
    /// <param name="node">The selected node.</param>
    /// <returns>A task that completes when the list has been replaced.</returns>
    private async Task LoadMessagesAsync(object? node)
    {
        var version = ++_loadVersion;
        if (node is not FolderNodeViewModel folder)
        {
            ReplaceMessages([]);
            return;
        }

        await GuardedAsync("Error_LoadMessages", async () =>
        {
            var messages = await _dispatcher.SendAsync(new GetMessagesQuery(folder.Folder.Id, 0, PageSize));
            if (version == _loadVersion)
            {
                ReplaceMessages(messages);
            }
        });
    }

    /// <summary>
    /// Replaces the message list, keeping the current selection when the message is still listed.
    /// </summary>
    /// <param name="messages">The new messages.</param>
    private void ReplaceMessages(IReadOnlyList<MailMessage> messages)
    {
        var selectedId = SelectedMessage?.Id;
        SelectedMessage = null;
        Messages.Clear();
        foreach (var message in messages)
        {
            Messages.Add(new MessageItemViewModel(message, SaveAttachmentAsync, _localizer));
        }

        if (selectedId is { } id)
        {
            SelectedMessage = Messages.FirstOrDefault(m => m.Id == id);
        }
    }

    /// <summary>
    /// Gets the account a tree selection belongs to.
    /// </summary>
    /// <param name="node">The selected node.</param>
    /// <returns>The account identifier, or <see langword="null"/> when nothing is selected.</returns>
    private static Guid? ScopeAccountId(object? node)
    {
        return node switch
        {
            FolderNodeViewModel folder => folder.Account.Id,
            AccountNodeViewModel account => account.Account.Id,
            _ => null,
        };
    }

    /// <summary>
    /// Runs an action and shows any failure in the status line instead of letting it escape to the UI thread.
    /// </summary>
    /// <param name="failureKey">The resource key of the text preceding the error message.</param>
    /// <param name="action">The action to run.</param>
    /// <returns>A task that completes when the action has finished or failed.</returns>
    private async Task GuardedAsync(string failureKey, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // This is the UI boundary: report the failure to the user rather than crashing the application.
            StatusText = _localizer.Format("Error_Format", _localizer[failureKey], ex.Message);
        }
    }
}
