using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Labels;
using Swiftlet.Application.Messages;
using Swiftlet.Application.Queries;
using Swiftlet.Application.Sync;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Services;
using Swiftlet.Desktop.ViewModels.Compose;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Labels;
using Swiftlet.Domain.Messages;
using AccountCommands = Swiftlet.Application.Accounts;

namespace Swiftlet.Desktop.ViewModels.Main;

/// <summary>
/// The view model of the main three-pane window: folder tree, message list and reading pane.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// The number of messages loaded per page of a folder, unified inbox, label or search view.
    /// </summary>
    private const int PageSize = 50;

    /// <summary>
    /// How long after checking a folder against the server opening it again does not check it again.
    /// </summary>
    private static readonly TimeSpan FolderCheckInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// When each folder was last checked against the server because the user opened it.
    /// </summary>
    private readonly Dictionary<Guid, DateTimeOffset> _folderChecks = [];

    /// <summary>
    /// The unread counts being read.
    /// </summary>
    private Task _unreadTask = Task.CompletedTask;

    /// <summary>
    /// Numbers the unread count requests of each account, so an answer that was overtaken is discarded.
    /// </summary>
    private readonly Dictionary<Guid, int> _unreadVersions = [];

    /// <summary>
    /// The clock that spaces out folder checks.
    /// </summary>
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// The folder checks started by opening folders.
    /// </summary>
    private Task _folderCheckTask = Task.CompletedTask;

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
    /// The labels last loaded, offered in the per-message label menu.
    /// </summary>
    private IReadOnlyList<Label> _labels = [];

    /// <summary>
    /// Incremented for every message list load so slower, outdated loads can be discarded.
    /// </summary>
    private int _loadVersion;

    /// <summary>
    /// The number of long-running operations in progress.
    /// </summary>
    private int _busyCount;

    /// <summary>
    /// The most recent message list load.
    /// </summary>
    private Task _loadTask = Task.CompletedTask;

    /// <summary>
    /// The trimmed search text of the results currently listed, or <see langword="null"/> when a folder, unified inbox or label view is shown.
    /// </summary>
    private string? _shownSearchText;

    /// <summary>
    /// Whether the local database may hold more messages for the current view than the ones listed.
    /// </summary>
    private bool _hasMore;

    /// <summary>
    /// Whether a next page is being loaded, so repeated scroll events do not request it twice.
    /// </summary>
    private bool _isLoadingMore;

    /// <summary>
    /// The most recent selection side effect (marking as read).
    /// </summary>
    private Task _selectionTask = Task.CompletedTask;

    /// <summary>
    /// The saved rules that allow remote images for senders.
    /// </summary>
    private readonly IRemoteImageRules _remoteImageRules;

    /// <summary>
    /// The sync notifications.
    /// </summary>
    private readonly ISyncEvents _syncEvents;

    /// <summary>
    /// Marshals sync-triggered refreshes to the UI thread.
    /// </summary>
    private readonly IUiThread _uiThread;

    /// <summary>
    /// Guards the coalescing flags, which are touched from background threads.
    /// </summary>
    private readonly Lock _refreshGate = new();

    /// <summary>
    /// Whether a sync-triggered refresh is scheduled or running.
    /// </summary>
    private bool _syncRefreshRunning;

    /// <summary>
    /// Whether another sync arrived while a refresh was running.
    /// </summary>
    private bool _syncRefreshPending;

    /// <summary>
    /// Whether the view model was disposed.
    /// </summary>
    private bool _disposed;

    /// <summary>
    /// The logger.
    /// </summary>
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MainWindowViewModel"/> class.
    /// </summary>
    /// <param name="dispatcher">The request dispatcher.</param>
    /// <param name="dialogs">The dialog service.</param>
    /// <param name="localizer">The localizer.</param>
    /// <param name="syncEvents">The sync notifications that trigger a refresh.</param>
    /// <param name="uiThread">The UI thread marshaller.</param>
    /// <param name="remoteImageRules">The saved rules that allow remote images for senders.</param>
    /// <param name="logger">The logger; optional.</param>
    /// <param name="timeProvider">The clock; optional.</param>
    public MainWindowViewModel(
        IRequestDispatcher dispatcher,
        IDialogService dialogs,
        ILocalizer localizer,
        ISyncEvents syncEvents,
        IUiThread uiThread,
        IRemoteImageRules remoteImageRules,
        ILogger<MainWindowViewModel>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _logger = logger ?? NullLogger<MainWindowViewModel>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _remoteImageRules = remoteImageRules;
        _syncEvents = syncEvents;
        _uiThread = uiThread;
        _dispatcher = dispatcher;
        _dialogs = dialogs;
        _localizer = localizer;
        SearchText = string.Empty;
        StatusText = string.Empty;
        UnifiedInbox = new UnifiedInboxNodeViewModel(localizer);
        TreeRoots.Add(UnifiedInbox);
        localizer.LanguageChanged += (_, _) => RefreshLanguage();
        _syncEvents.AccountSynced += OnAccountSynced;
        _syncEvents.BackfillProgressed += OnBackfillProgressed;
        _syncEvents.DeletionProgressed += OnDeletionProgressed;
        _syncEvents.MessageSent += OnMessageSent;
        SelectedMessages.CollectionChanged += (_, _) => DeleteSelectedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Stops reacting to sync notifications.
    /// </summary>
    public void Dispose()
    {
        lock (_refreshGate)
        {
            _disposed = true;
        }

        _syncEvents.AccountSynced -= OnAccountSynced;
        _syncEvents.BackfillProgressed -= OnBackfillProgressed;
        _syncEvents.DeletionProgressed -= OnDeletionProgressed;
        _syncEvents.MessageSent -= OnMessageSent;
    }

    /// <summary>
    /// Synchronizes the Sent folder of an account that just sent a message, so the copy shows without waiting; may be called from any thread.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private void OnMessageSent(object? sender, MessageSentEventArgs e)
    {
        lock (_refreshGate)
        {
            if (_disposed)
            {
                return;
            }
        }

        _ = ObserveAsync(_uiThread.InvokeAsync(() =>
        {
            var sentFolders = Accounts
                .Where(a => a.Account.Id == e.AccountId)
                .SelectMany(a => a.Folders)
                .Where(f => f.Folder.Role == FolderRole.Sent);
            foreach (var node in sentFolders)
            {
                // The folder changed just now, so an earlier check does not count.
                StartFolderCheck(node.Folder);
            }

            return Task.CompletedTask;
        }));
    }

    /// <summary>
    /// Shows how the background deletion is doing and reloads the view when a run ends or messages come back; may be called from any thread.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private void OnDeletionProgressed(object? sender, DeletionProgressEventArgs e)
    {
        lock (_refreshGate)
        {
            if (_disposed)
            {
                return;
            }
        }

        _ = ObserveAsync(_uiThread.InvokeAsync(() =>
        {
            if (e.Error is { } error)
            {
                var failure = e.WillRetry ? _localizer["Status_DeleteRetrying"] : _localizer["Error_DeleteRejected"];
                StatusText = _localizer.Format("Error_Format", failure, error);
            }
            else if (e.Finished)
            {
                StatusText = _localizer.Format("Status_DeleteDone", e.Deleted);
            }
            else
            {
                StatusText = _localizer.Format("Status_Deleting", e.Deleted, e.Remaining);
            }

            // Moved messages appear in Trash and refused ones come back, so the view is reloaded.
            if (e.Finished || (e.Error is not null && !e.WillRetry))
            {
                ScheduleRefresh();
            }

            return Task.CompletedTask;
        }));
    }

    /// <summary>
    /// Shows how far the background history download has come; may be called from any thread.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private void OnBackfillProgressed(object? sender, BackfillProgressEventArgs e)
    {
        lock (_refreshGate)
        {
            if (_disposed)
            {
                return;
            }
        }

        _ = ObserveAsync(_uiThread.InvokeAsync(() =>
        {
            if (e.Finished)
            {
                StatusText = _localizer.Format("Status_HistoryDone", e.Downloaded);
            }
            else if (e.FolderName is { } folderName)
            {
                // Name the account and folder and how many messages of the folder are still to come, so the user can see the download move.
                var account = _accounts.FirstOrDefault(a => a.Id == e.AccountId)?.EmailAddress ?? string.Empty;
                StatusText = _localizer.Format("Status_HistoryDownloadingFolder", e.Downloaded, account, folderName, e.Remaining);
            }
            else
            {
                StatusText = _localizer.Format("Status_HistoryDownloading", e.Downloaded);
            }

            return Task.CompletedTask;
        }));
    }

    /// <summary>
    /// Refreshes the view after an account was synchronized; may be called from any thread.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private void OnAccountSynced(object? sender, AccountSyncedEventArgs e)
    {
        ScheduleRefresh();
    }

    /// <summary>
    /// Schedules a coalesced refresh on the UI thread; may be called from any thread.
    /// </summary>
    private void ScheduleRefresh()
    {
        lock (_refreshGate)
        {
            if (_disposed)
            {
                return;
            }

            if (_syncRefreshRunning)
            {
                _syncRefreshPending = true;
                return;
            }

            _syncRefreshRunning = true;
        }

        _ = ObserveAsync(_uiThread.InvokeAsync(RunSyncRefreshesAsync));
    }

    /// <summary>
    /// Refreshes, then once more if syncs arrived meanwhile; never overlaps and never runs more than one follow-up.
    /// </summary>
    /// <returns>A task that completes when no refresh is pending.</returns>
    private async Task RunSyncRefreshesAsync()
    {
        while (true)
        {
            try
            {
                await RefreshAsync();
            }
            catch
            {
                // Release the running flag so later syncs can refresh again, then let the observer handle the failure.
                lock (_refreshGate)
                {
                    _syncRefreshRunning = false;
                    _syncRefreshPending = false;
                }

                throw;
            }

            lock (_refreshGate)
            {
                if (_disposed || !_syncRefreshPending)
                {
                    _syncRefreshRunning = false;
                    return;
                }

                _syncRefreshPending = false;
            }
        }
    }

    /// <summary>
    /// Observes a fire-and-forget task so a failure never goes unobserved or crashes the process.
    /// </summary>
    /// <param name="task">The task to observe.</param>
    /// <returns>A task that completes when the observed task has finished.</returns>
    private async Task ObserveAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // This is the UI boundary of a background notification: RefreshAsync already reports failures, so anything left (e.g. a marshaller error) is dropped and the next sync or timer tick retries.
            lock (_refreshGate)
            {
                _syncRefreshRunning = false;
                _syncRefreshPending = false;
            }
        }
    }

    /// <summary>
    /// Gets the unified inbox node.
    /// </summary>
    public UnifiedInboxNodeViewModel UnifiedInbox { get; }

    /// <summary>
    /// Gets the top-level tree nodes: the unified inbox, then the account nodes, then the label nodes.
    /// </summary>
    public ObservableCollection<object> TreeRoots { get; } = [];

    /// <summary>
    /// Gets the account nodes of the folder tree.
    /// </summary>
    public ObservableCollection<AccountNodeViewModel> Accounts { get; } = [];

    /// <summary>
    /// Gets the messages of the selected folder or search.
    /// </summary>
    public ObservableCollection<MessageItemViewModel> Messages { get; } = [];

    /// <summary>
    /// Gets the messages selected in the list; the list control keeps it up to date.
    /// </summary>
    public ObservableCollection<MessageItemViewModel> SelectedMessages { get; } = [];

    /// <summary>
    /// Gets one checkable entry per existing label for the selected message; empty when no message is selected.
    /// </summary>
    public ObservableCollection<MessageLabelChoiceViewModel> MessageLabelChoices { get; } = [];

    /// <summary>
    /// Gets or sets the selected tree node: the unified inbox, an account, a folder or a label.
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
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedCommand))]
    [NotifyPropertyChangedFor(nameof(IsNoMessageSelected))]
    public partial MessageItemViewModel? SelectedMessage { get; set; }

    /// <summary>
    /// Gets a value indicating whether no message is selected, so the reading pane shows its placeholder.
    /// </summary>
    public bool IsNoMessageSelected => SelectedMessage is null;

    /// <summary>
    /// Gets or sets why the message list has nothing to show.
    /// </summary>
    [ObservableProperty]
    public partial EmptyState ListEmptyState { get; set; }

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
    /// Waits until pending list loads, selection side effects and folder checks have finished.
    /// </summary>
    /// <returns>A task that completes when the view model is idle.</returns>
    internal async Task WhenIdleAsync()
    {
        await _loadTask;
        await _selectionTask;
        await _folderCheckTask;
        await _unreadTask;
    }

    /// <summary>
    /// Opens the add-account dialog and synchronizes the new account.
    /// Runs concurrently, so a first sync in progress does not block adding the next account.
    /// </summary>
    /// <returns>A task that completes when the dialog and the first sync are done.</returns>
    [RelayCommand(AllowConcurrentExecutions = true)]
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
        _logger.LogInformation("Manual sync requested");
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
            _loadTask = LoadMessagesAsync(SelectedNode, keepLoaded: true);
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

        _logger.LogInformation("Searching ({Length} characters)", text.Length);
        var version = ++_loadVersion;
        ListEmptyState = EmptyState.None;
        await GuardedAsync("Error_Search", async () =>
        {
            var results = await FetchPageAsync(SelectedNode, text, 0, PageSize);
            if (version != _loadVersion)
            {
                return;
            }

            _shownSearchText = text;
            _hasMore = results.Count >= PageSize;
            ReplaceMessages(results);
            ShowEmptyState();
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
            await _dispatcher.SendAsync(new SetMessageFlaggedCommand(item.Message.AccountId, item.Id, flagged));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Roll back the optimistic update and tell the user; a failed star must not crash the UI.
            item.IsFlagged = !flagged;
            StatusText = _localizer.Format("Error_Format", _localizer["Error_Flag"], ex.Message);
        }
    }

    /// <summary>
    /// Deletes the selected messages after the user confirmed. They leave the list at once; the mail server is told in the background.
    /// </summary>
    /// <returns>A task that completes when the deletion has been queued or cancelled.</returns>
    [RelayCommand(CanExecute = nameof(HasSelection), AllowConcurrentExecutions = true)]
    private async Task DeleteSelectedAsync()
    {
        var targets = DeletionTargets();
        if (targets.Count == 0 || !await ConfirmDeletionAsync(targets))
        {
            return;
        }

        var keys = targets.Select(t => new MessageKey(t.Message.AccountId, t.Id)).ToList();
        RemoveFromList(targets);
        await GuardedAsync("Error_DeleteMessage", async () =>
        {
            await _dispatcher.SendAsync(new DeleteMessagesCommand(keys));
            StatusText = _localizer.Format("Status_DeleteQueued", keys.Count);
        });
        foreach (var accountId in keys.Select(k => k.AccountId).Distinct())
        {
            StartUnreadRefresh(accountId);
        }

        // Fills the page up again, and brings the messages back if they could not be queued.
        await ReloadViewAsync();
    }

    /// <summary>
    /// Deletes every message of a folder after the user confirmed, including those that were never downloaded.
    /// </summary>
    /// <param name="node">The folder node the menu was opened on.</param>
    /// <returns>A task that completes when the deletion has been queued or cancelled.</returns>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task EmptyFolderAsync(FolderNodeViewModel? node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!node.CanEmpty)
        {
            return;
        }

        var permanent = node.Folder.Role == FolderRole.Trash || !HasTrash(node.Folder.AccountId);
        var text = permanent
            ? _localizer.Format("Confirm_EmptyFolder_Permanent", node.Name, node.Account.EmailAddress)
            : _localizer.Format("Confirm_EmptyFolder_ToTrash", node.Name, node.Account.EmailAddress);
        if (!await _dialogs.ConfirmAsync(_localizer["Confirm_EmptyFolder_Title"], text, _localizer["Toolbar_Delete"]))
        {
            return;
        }

        var queued = false;
        await GuardedAsync("Error_EmptyFolder", async () =>
        {
            queued = await _dispatcher.SendAsync(new EmptyFolderCommand(node.Folder.AccountId, node.Folder.Id));
            StatusText = queued
                ? _localizer.Format("Status_EmptyQueued", node.Name)
                : _localizer.Format("Status_NothingToDelete", node.Name);
        });
        if (queued)
        {
            StartUnreadRefresh(node.Folder.AccountId);
            RemoveFromList([.. Messages.Where(m => m.Message.FolderId == node.Folder.Id)]);
            await ReloadViewAsync();
        }
    }

    /// <summary>
    /// Gets the messages a delete applies to, in list order: the selection, or the message shown in the reading pane.
    /// </summary>
    /// <returns>The messages to delete.</returns>
    private List<MessageItemViewModel> DeletionTargets()
    {
        var selected = SelectedMessages.ToHashSet();
        if (selected.Count == 0 && SelectedMessage is { } single)
        {
            selected.Add(single);
        }

        return [.. Messages.Where(selected.Contains)];
    }

    /// <summary>
    /// Asks the user once to confirm a deletion, saying whether the messages can still be recovered from Trash.
    /// </summary>
    /// <param name="targets">The messages to delete.</param>
    /// <returns><see langword="true"/> if the user confirmed.</returns>
    private async Task<bool> ConfirmDeletionAsync(List<MessageItemViewModel> targets)
    {
        var permanent = targets.Count(t => WillDeletePermanently(t.Message));
        string text;
        if (targets.Count == 1)
        {
            text = permanent == 1
                ? _localizer.Format("Confirm_Delete_Permanent", targets[0].Subject)
                : _localizer.Format("Confirm_Delete_ToTrash", targets[0].Subject);
        }
        else if (permanent == 0)
        {
            text = _localizer.Format("Confirm_DeleteMany_ToTrash", targets.Count);
        }
        else if (permanent == targets.Count)
        {
            text = _localizer.Format("Confirm_DeleteMany_Permanent", targets.Count);
        }
        else
        {
            text = _localizer.Format("Confirm_DeleteMany_Mixed", targets.Count - permanent, permanent);
        }

        return await _dialogs.ConfirmAsync(_localizer["Confirm_Delete_Title"], text, _localizer["Toolbar_Delete"]);
    }

    /// <summary>
    /// Takes messages out of the list at once. If the shown message is among them, the next remaining one is selected.
    /// </summary>
    /// <param name="removed">The messages to remove.</param>
    private void RemoveFromList(List<MessageItemViewModel> removed)
    {
        if (removed.Count == 0)
        {
            return;
        }

        // A load that started before the deletion would bring the messages back, so its result is discarded.
        _loadVersion++;
        var gone = removed.ToHashSet();
        MessageItemViewModel? next = null;
        if (SelectedMessage is { } shown && gone.Contains(shown))
        {
            var index = Math.Max(0, Messages.IndexOf(shown));
            next = Messages.Skip(index).FirstOrDefault(m => !gone.Contains(m))
                ?? Messages.Take(index).LastOrDefault(m => !gone.Contains(m));
            SelectedMessage = null;
        }

        SelectedMessages.Clear();
        foreach (var item in gone)
        {
            Messages.Remove(item);
        }

        if (next is not null)
        {
            SelectedMessage = next;
        }
    }

    /// <summary>
    /// Reloads the listed view, a search included, keeping the pages already loaded.
    /// </summary>
    /// <returns>A task that completes when the list has been replaced.</returns>
    private async Task ReloadViewAsync()
    {
        if (_shownSearchText is not { } search)
        {
            _loadTask = LoadMessagesAsync(SelectedNode, keepLoaded: true);
            await _loadTask;
            return;
        }

        var version = ++_loadVersion;
        var take = Math.Max(PageSize, Messages.Count);
        await GuardedAsync("Error_Search", async () =>
        {
            var results = await FetchPageAsync(SelectedNode, search, 0, take);
            if (version == _loadVersion)
            {
                _hasMore = results.Count >= take;
                ReplaceMessages(results);
                ShowEmptyState();
            }
        });
    }

    /// <summary>
    /// Removes an account and its local data after the user confirmed; the mailbox on the server is not touched.
    /// </summary>
    /// <param name="node">The account node the menu was opened on.</param>
    /// <returns>A task that completes when the account has been removed or the action was cancelled.</returns>
    [RelayCommand]
    private async Task DeleteAccountAsync(AccountNodeViewModel? node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var address = node.Account.EmailAddress;
        if (!await _dialogs.ConfirmAsync(
                _localizer["Confirm_DeleteAccount_Title"],
                _localizer.Format("Confirm_DeleteAccount_Text", address),
                _localizer["Menu_Account_Delete"]))
        {
            return;
        }

        var deleted = false;
        await GuardedAsync("Error_DeleteAccount", async () =>
        {
            await _dispatcher.SendAsync(new AccountCommands.DeleteAccountCommand(node.Account.Id));
            deleted = true;
        });
        var deleteError = deleted ? null : StatusText;

        // Reload even after a failure: the rows may already be gone (for example when only the secret removal failed),
        // and the tree must not keep a node for an account that no longer exists.
        await GuardedAsync("Error_LoadAccounts", ReloadAccountsAsync);
        if (deleted || !Accounts.Contains(node))
        {
            // Cross-account views (unified inbox, labels, search results) may still list the removed account's messages.
            await SearchAsync();
        }

        StatusText = deleted ? _localizer.Format("Status_AccountDeleted", address) : deleteError ?? StatusText;
    }

    /// <summary>
    /// Signs an OAuth account in again in the browser, then synchronizes it.
    /// </summary>
    /// <param name="node">The account node the menu was opened on.</param>
    /// <returns>A task that completes when the sign-in and the follow-up sync are done.</returns>
    [RelayCommand]
    private async Task ReauthenticateAccountAsync(AccountNodeViewModel? node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var account = node.Account;
        var signedIn = false;
        BeginBusy();
        try
        {
            StatusText = _localizer.Format("Status_Reauthenticating", account.EmailAddress);
            await GuardedAsync("Error_Reauthenticate", async () =>
            {
                await _dispatcher.SendAsync(new AccountCommands.ReauthenticateOAuthAccountCommand(account.Id));
                signedIn = true;
            });
        }
        finally
        {
            EndBusy();
        }

        if (signedIn)
        {
            await SyncAccountsAsync([account]);
        }
    }

    /// <summary>
    /// Determines whether deleting a message removes it for good: it already sits in a Trash folder, or its account has none.
    /// </summary>
    /// <param name="message">The message to delete.</param>
    /// <returns><see langword="true"/> if the deletion cannot be undone on the server.</returns>
    private bool WillDeletePermanently(MailMessage message)
    {
        var folders = Accounts.FirstOrDefault(a => a.Account.Id == message.AccountId)?.Folders;
        if (folders is null)
        {
            // The account is not loaded, so the Trash folder cannot be ruled out; the stronger wording is the safe one.
            return true;
        }

        var inTrash = folders.Any(f => f.Folder.Id == message.FolderId && f.Folder.Role == FolderRole.Trash);
        return inTrash || !HasTrash(message.AccountId);
    }

    /// <summary>
    /// Determines whether a loaded account has a Trash folder.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <returns><see langword="true"/> if deleted messages of the account can be moved to Trash.</returns>
    private bool HasTrash(Guid accountId)
    {
        return Accounts.Any(a => a.Account.Id == accountId && a.Folders.Any(f => f.Folder.Role == FolderRole.Trash));
    }

    /// <summary>
    /// Opens the settings dialog.
    /// </summary>
    /// <returns>A task that completes when the dialog has been closed.</returns>
    [RelayCommand]
    private async Task OpenSettingsAsync()
    {
        await _dialogs.ShowSettingsAsync();
    }

    /// <summary>
    /// Opens the label manager and reloads the labels, and the list they decorate, afterwards.
    /// </summary>
    /// <returns>A task that completes when the manager has been closed and the view is up to date.</returns>
    [RelayCommand]
    private async Task ManageLabelsAsync()
    {
        await _dialogs.ShowLabelManagerAsync();
        await RefreshAsync();
    }

    /// <summary>
    /// Attaches a label to the selected message or removes it, following the state of the choice.
    /// </summary>
    /// <param name="choice">The ticked or unticked label entry; its <see cref="MessageLabelChoiceViewModel.IsAssigned"/> holds the desired state.</param>
    /// <returns>A task that completes when the state has been saved.</returns>
    [RelayCommand]
    private async Task ToggleMessageLabelAsync(MessageLabelChoiceViewModel? choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        if (SelectedMessage is not { } item)
        {
            return;
        }

        var assigned = choice.IsAssigned;
        try
        {
            await _dispatcher.SendAsync(new SetMessageLabelCommand(item.Message.AccountId, item.Id, choice.Label.Id, assigned));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Roll back the optimistic tick and tell the user; a failed label must not crash the UI.
            choice.IsAssigned = !assigned;
            StatusText = _localizer.Format("Error_Format", _localizer["Error_SetLabel"], ex.Message);
            return;
        }

        if (assigned)
        {
            item.Message.AddLabel(choice.Label);
        }
        else
        {
            item.Message.RemoveLabel(choice.Label.Id);
        }

        item.RefreshLabels();
        if (!assigned && SelectedNode is LabelNodeViewModel node && node.Label.Id == choice.Label.Id)
        {
            // The label view lists messages carrying the label, so the message no longer belongs there.
            SelectedMessage = null;
            Messages.Remove(item);
            ShowEmptyState();
        }
    }

    /// <summary>
    /// Opens the HTML body of the selected message in the system browser.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanOpenHtml))]
    private void OpenHtml()
    {
        _logger.LogInformation("Opening message {MessageId} in the browser", SelectedMessage!.Id);
        _dialogs.ShowHtmlInBrowser(SelectedMessage!.Message.BodyHtml!);
    }

    /// <summary>
    /// Gets a value indicating whether a message is selected.
    /// </summary>
    private bool HasSelectedMessage => SelectedMessage is not null;

    /// <summary>
    /// Gets a value indicating whether there is at least one message to delete.
    /// </summary>
    private bool HasSelection => SelectedMessage is not null || SelectedMessages.Count > 0;

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
        _logger.LogInformation("Selected {Node}", value?.GetType().Name);
        SearchText = string.Empty;
        _loadTask = LoadMessagesAsync(value);
        if (value is FolderNodeViewModel folder)
        {
            CheckFolderIfDue(folder.Folder);
        }
    }

    /// <summary>
    /// Starts a server check of an opened folder, so mail deleted or changed elsewhere shows without waiting for the next account sync.
    /// Skipped when the folder was checked a moment ago, which also covers the tree being rebuilt around the same selection.
    /// </summary>
    /// <param name="folder">The opened folder.</param>
    private void CheckFolderIfDue(MailFolder folder)
    {
        if (_folderChecks.TryGetValue(folder.Id, out var last) && _timeProvider.GetUtcNow() - last < FolderCheckInterval)
        {
            return;
        }

        StartFolderCheck(folder);
    }

    /// <summary>
    /// Starts a server check of a folder now and remembers when it was started.
    /// </summary>
    /// <param name="folder">The folder to check.</param>
    private void StartFolderCheck(MailFolder folder)
    {
        _folderChecks[folder.Id] = _timeProvider.GetUtcNow();
        _folderCheckTask = Task.WhenAll(_folderCheckTask, CheckFolderAsync(folder.AccountId, folder.Id));
    }

    /// <summary>
    /// Synchronizes one folder off the UI thread; a change is shown through the sync notification.
    /// </summary>
    /// <param name="accountId">The owning account.</param>
    /// <param name="folderId">The folder.</param>
    /// <returns>A task that completes when the check has ended.</returns>
    private async Task CheckFolderAsync(Guid accountId, Guid folderId)
    {
        try
        {
            await Task.Run(() => _dispatcher.SendAsync(new SyncFolderCommand(accountId, folderId)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The cached messages stay listed, and the regular sync retries and reports a lasting problem.
            _logger.LogWarning(ex, "Checking folder {FolderId} of account {AccountId} failed", folderId, accountId);
        }
    }

    /// <summary>
    /// Refreshes folder names and placeholder subjects after the UI language changed.
    /// </summary>
    private void RefreshLanguage()
    {
        UnifiedInbox.RefreshLanguage();
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
        _logger.LogDebug("Selected message {MessageId}", value?.Id);
        RebuildLabelChoices();
        var sideEffects = new List<Task>();
        if (value is { IsRead: false })
        {
            sideEffects.Add(MarkReadAsync(value));
        }

        if (value is { AllowRemoteImages: false } && value.HasBlockedRemoteImages && _remoteImageRules.IsAllowed(value.Message.FromAddress))
        {
            value.AllowRemoteImages = true;
        }

        if (value is { HasHtml: true, InlineImagesRequested: false } && value.HasInlineImageReferences)
        {
            value.InlineImagesRequested = true;
            sideEffects.Add(LoadInlineImagesAsync(value));
        }

        if (sideEffects.Count > 0)
        {
            _selectionTask = Task.WhenAll(sideEffects);
        }
    }

    /// <summary>
    /// Saves a rule that always allows remote images and reports a failure; the message that asked is already allowed.
    /// </summary>
    /// <param name="item">The message the user chose the rule for.</param>
    /// <param name="rule">The sender address or domain.</param>
    /// <returns>A task that completes when the rule is saved or the failure is reported.</returns>
    private async Task RememberRemoteImagesAsync(MessageItemViewModel item, string rule)
    {
        try
        {
            await _remoteImageRules.AddAsync(rule);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The images are already shown for this message; only the saved rule is missing, so this is just reported.
            StatusText = _localizer.Format("Error_Format", _localizer["Error_SaveImageRule"], ex.Message);
        }
    }

    /// <summary>
    /// Downloads the images embedded in a message from the server and shows them in the reading pane.
    /// </summary>
    /// <param name="item">The message.</param>
    /// <returns>A task that completes when the images are shown or the failure is reported.</returns>
    private async Task LoadInlineImagesAsync(MessageItemViewModel item)
    {
        try
        {
            var images = await _dispatcher.SendAsync(new GetInlineImagesQuery(item.Message.AccountId, item.Id));
            item.SetInlineImages(images);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The text of the message is readable without the pictures, so this is only reported; selecting the message again retries.
            item.InlineImagesRequested = false;
            StatusText = _localizer.Format("Error_Format", _localizer["Error_InlineImages"], ex.Message);
        }
    }

    /// <summary>
    /// Rebuilds the per-message label menu for the selected message.
    /// </summary>
    private void RebuildLabelChoices()
    {
        MessageLabelChoices.Clear();
        if (SelectedMessage is not { } item)
        {
            return;
        }

        foreach (var label in _labels)
        {
            MessageLabelChoices.Add(new MessageLabelChoiceViewModel(label, item.Message.Labels.Any(l => l.Id == label.Id), ToggleMessageLabelCommand));
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
            await _dispatcher.SendAsync(new SetMessageReadCommand(item.Message.AccountId, item.Id, true));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Roll back the optimistic update; the message simply stays unread.
            item.IsRead = false;
            StatusText = _localizer.Format("Error_Format", _localizer["Error_MarkRead"], ex.Message);
            return;
        }

        StartUnreadRefresh(item.Message.AccountId);
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

        _logger.LogInformation("Saving attachment {Ordinal} of message {MessageId}", attachment.Attachment.Ordinal, attachment.Attachment.MessageId);

        await GuardedAsync("Error_SaveAttachment", async () =>
        {
            StatusText = _localizer.Format("Status_Downloading", attachment.Attachment.FileName);
            var downloaded = await _dispatcher.SendAsync(
                new DownloadAttachmentQuery(attachment.AccountId, attachment.Attachment.MessageId, attachment.Attachment.Id));
            await File.WriteAllBytesAsync(path, downloaded.Content);
            StatusText = _localizer.Format("Status_Saved", path);
        });
    }

    /// <summary>
    /// Synchronizes the given accounts side by side and reloads the view.
    /// </summary>
    /// <param name="targets">The accounts to synchronize.</param>
    /// <returns>A task that completes when all accounts have been attempted.</returns>
    private async Task SyncAccountsAsync(List<MailAccount> targets)
    {
        if (targets.Count == 0)
        {
            return;
        }

        BeginBusy();
        try
        {
            var running = targets.ToList();
            ShowRunningSyncs(running);

            // WhenAll keeps the order of the accounts, so the problems are listed in a stable order.
            var outcomes = await Task.WhenAll(targets.Select(account => SyncOneAccountAsync(account, running)));
            var newMessages = outcomes.Sum(o => o.NewMessages);
            var problems = outcomes.Select(o => o.Problem).OfType<string>().ToList();

            await GuardedAsync("Error_Refresh", ReloadAccountsAsync);
            _loadTask = LoadMessagesAsync(SelectedNode, keepLoaded: true);
            await _loadTask;
            StatusText = problems.Count == 0
                ? _localizer.Format("Status_SyncDone", newMessages)
                : _localizer.Format("Status_SyncDoneWithProblems", newMessages, string.Join(_localizer["Status_ProblemSeparator"], problems));
        }
        finally
        {
            EndBusy();
        }
    }

    /// <summary>
    /// Counts a long-running operation as started.
    /// </summary>
    private void BeginBusy()
    {
        _busyCount++;
        IsBusy = true;
    }

    /// <summary>
    /// Counts a long-running operation as finished; the view stays busy while others still run.
    /// </summary>
    private void EndBusy()
    {
        _busyCount--;
        IsBusy = _busyCount > 0;
    }

    /// <summary>
    /// Synchronizes one account of a manual sync and updates the status line when it is done.
    /// </summary>
    /// <param name="account">The account.</param>
    /// <param name="running">The accounts still being synchronized; the finished account is removed.</param>
    /// <returns>The number of new messages and the problem to report, if any.</returns>
    private async Task<(int NewMessages, string? Problem)> SyncOneAccountAsync(MailAccount account, List<MailAccount> running)
    {
        try
        {
            var result = await _dispatcher.SendAsync(new SyncAccountCommand(account.Id));
            var problem = result.FailedFolders.Count > 0
                ? _localizer.Format("Status_AccountFoldersFailed", account.EmailAddress, result.FailedFolders.Count)
                : null;
            return (result.NewMessages, problem);
        }
        catch (OAuthReauthenticationRequiredException)
        {
            // Not a transient failure: the stored sign-in is gone or revoked, so point the user at the fix.
            return (0, _localizer.Format("Status_ReauthenticationRequired", account.EmailAddress));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One unreachable account must not prevent the others from synchronizing.
            return (0, $"{account.EmailAddress}: {ex.Message}");
        }
        finally
        {
            // Without a UI thread (tests) two accounts may finish at the same moment.
            lock (running)
            {
                running.Remove(account);
                ShowRunningSyncs(running);
            }
        }
    }

    /// <summary>
    /// Names the accounts still being synchronized in the status line.
    /// </summary>
    /// <param name="running">The accounts.</param>
    private void ShowRunningSyncs(List<MailAccount> running)
    {
        if (running.Count > 0)
        {
            StatusText = _localizer.Format("Status_Syncing", string.Join(", ", running.Select(a => a.EmailAddress)));
        }
    }

    /// <summary>
    /// Merges the accounts and folders from the database into the tree without disturbing unchanged nodes.
    /// </summary>
    /// <returns>A task that completes when the tree is up to date.</returns>
    private async Task ReloadAccountsAsync()
    {
        _accounts = await _dispatcher.SendAsync(new GetAccountsQuery());

        var present = _accounts.Select(a => a.Id).ToHashSet();
        foreach (var gone in Accounts.Where(n => !present.Contains(n.Account.Id)).ToList())
        {
            // Drop the node first so a selection inside it can be repaired below.
            Accounts.Remove(gone);
            TreeRoots.Remove(gone);
            if (ScopeAccountId(SelectedNode) == gone.Account.Id)
            {
                SelectedNode = null;
            }
        }

        foreach (var account in _accounts)
        {
            var node = Accounts.FirstOrDefault(n => n.Account.Id == account.Id);
            if (node is null)
            {
                if (_accounts.All(a => a.Id != account.Id))
                {
                    // A newer reload removed this account while this one was awaiting folders; do not bring its node back.
                    continue;
                }

                node = new AccountNodeViewModel(account);
                TreeRoots.Insert(1 + Accounts.Count, node);
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

        await ReloadLabelsAsync();

        // The unified inbox is the view at startup and whenever the selected node disappears.
        if (SelectedNode is null && Accounts.Count > 0)
        {
            SelectedNode = UnifiedInbox;
        }

        if (Accounts.Count == 0)
        {
            ListEmptyState = EmptyState.NoAccounts;
        }
        else if (ListEmptyState == EmptyState.NoAccounts)
        {
            ListEmptyState = EmptyState.None;
        }

        foreach (var node in Accounts.ToList())
        {
            StartUnreadRefresh(node.Account.Id);
        }

        // Also covers removed accounts, for which nothing is queried.
        UpdateUnifiedUnread();
    }

    /// <summary>
    /// Starts re-reading the unread counts of an account. Nothing waits for it, so lists, syncs and deletions are not
    /// held up by the badges.
    /// </summary>
    /// <param name="accountId">The account.</param>
    private void StartUnreadRefresh(Guid accountId)
    {
        _unreadTask = Task.WhenAll(_unreadTask, RefreshUnreadAsync(accountId));
    }

    /// <summary>
    /// Re-reads the unread counts of an account and shows them in the tree.
    /// </summary>
    /// <param name="accountId">The account.</param>
    /// <returns>A task that completes when the counts are shown or the failure is logged.</returns>
    private async Task RefreshUnreadAsync(Guid accountId)
    {
        int version;
        lock (_unreadVersions)
        {
            version = _unreadVersions.GetValueOrDefault(accountId) + 1;
            _unreadVersions[accountId] = version;
        }

        try
        {
            // Database calls complete synchronously, so they are moved off the UI thread.
            var counts = await Task.Run(() => _dispatcher.SendAsync(new GetUnreadCountsQuery(accountId)));

            // The application continues on the UI thread; the lock matters where there is none (tests).
            lock (_unreadVersions)
            {
                if (_unreadVersions[accountId] != version)
                {
                    // A later change asked again; its answer is the current one.
                    return;
                }

                foreach (var folder in Accounts.Where(a => a.Account.Id == accountId).SelectMany(a => a.Folders))
                {
                    folder.UnreadCount = counts.GetValueOrDefault(folder.Folder.Id);
                }

                UpdateUnifiedUnread();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The badges are a hint; the old ones stay, and the next reload tries again.
            _logger.LogWarning(ex, "Counting unread messages of account {AccountId} failed", accountId);
        }
    }

    /// <summary>
    /// Shows the sum of the inbox counts on the unified inbox.
    /// </summary>
    private void UpdateUnifiedUnread()
    {
        UnifiedInbox.UnreadCount = Accounts
            .SelectMany(a => a.Folders)
            .Where(f => f.Folder.Role == FolderRole.Inbox)
            .Sum(f => f.UnreadCount);
    }

    /// <summary>
    /// Replaces the label nodes at the end of the tree when the labels changed, keeping a selected label selected.
    /// </summary>
    /// <returns>A task that completes when the label nodes are up to date.</returns>
    private async Task ReloadLabelsAsync()
    {
        var labels = await _dispatcher.SendAsync(new GetLabelsQuery());
        _labels = labels;
        RebuildLabelChoices();
        var current = TreeRoots.OfType<LabelNodeViewModel>().ToList();
        if (current.Select(n => (n.Label.Id, n.Name, n.Color)).SequenceEqual(labels.Select(l => (l.Id, l.Name, l.Color))))
        {
            return;
        }

        var selectedLabelId = (SelectedNode as LabelNodeViewModel)?.Label.Id;
        foreach (var node in current)
        {
            TreeRoots.Remove(node);
        }

        var added = labels.Select((label, index) => new LabelNodeViewModel(label, index == 0)).ToList();
        foreach (var node in added)
        {
            TreeRoots.Add(node);
        }

        if (selectedLabelId is { } id)
        {
            SelectedNode = added.FirstOrDefault(n => n.Label.Id == id);
        }
    }

    /// <summary>
    /// Loads the messages for a tree selection and shows them.
    /// </summary>
    /// <param name="node">The selected node.</param>
    /// <param name="keepLoaded">
    /// <see langword="true"/> for a refresh of the same view: the pages already loaded by scrolling are fetched again, so the list is not cut back to the first page.
    /// </param>
    /// <returns>A task that completes when the list has been replaced.</returns>
    private async Task LoadMessagesAsync(object? node, bool keepLoaded = false)
    {
        var version = ++_loadVersion;
        _shownSearchText = null;
        if (node is not (FolderNodeViewModel or UnifiedInboxNodeViewModel or LabelNodeViewModel))
        {
            _hasMore = false;
            ReplaceMessages([]);
            ListEmptyState = Accounts.Count == 0 ? EmptyState.NoAccounts : EmptyState.None;
            return;
        }

        var take = keepLoaded ? Math.Max(PageSize, Messages.Count) : PageSize;
        if (!keepLoaded)
        {
            ListEmptyState = EmptyState.None;
        }

        await GuardedAsync("Error_LoadMessages", async () =>
        {
            var messages = await FetchPageAsync(node, null, 0, take);
            if (version == _loadVersion)
            {
                _hasMore = messages.Count >= take;
                ReplaceMessages(messages);
                ShowEmptyState();
            }
        });
    }

    /// <summary>
    /// Says why the list is empty, if it is; call it only after the list shows the complete result of a view or search.
    /// </summary>
    private void ShowEmptyState()
    {
        if (Accounts.Count == 0)
        {
            ListEmptyState = EmptyState.NoAccounts;
        }
        else if (Messages.Count > 0)
        {
            ListEmptyState = EmptyState.None;
        }
        else
        {
            ListEmptyState = _shownSearchText is null ? EmptyState.EmptyView : EmptyState.NoSearchResults;
        }
    }

    /// <summary>
    /// Loads the next page of the current view and appends it to the list; does nothing when everything is listed or a load is running.
    /// </summary>
    /// <returns>A task that completes when the page has been appended.</returns>
    [RelayCommand]
    public async Task LoadMoreAsync()
    {
        if (!_hasMore || _isLoadingMore)
        {
            return;
        }

        var node = SelectedNode;
        var search = _shownSearchText;
        var version = _loadVersion;
        var skip = Messages.Count;
        _isLoadingMore = true;
        try
        {
            await GuardedAsync("Error_LoadMessages", async () =>
            {
                var page = await FetchPageAsync(node, search, skip, PageSize);

                // A newer load (another folder, a search, a refresh) owns the list now; its result replaces this page.
                if (version != _loadVersion)
                {
                    return;
                }

                _hasMore = page.Count >= PageSize;
                var listed = Messages.Select(m => m.Id).ToHashSet();
                foreach (var message in page.Where(m => listed.Add(m.Id)))
                {
                    Messages.Add(CreateItem(message));
                }
            });
        }
        finally
        {
            _isLoadingMore = false;
        }
    }

    /// <summary>
    /// Fetches one page of the messages of a view.
    /// </summary>
    /// <param name="node">The selected node.</param>
    /// <param name="searchText">The search text, or <see langword="null"/> to list the node's messages.</param>
    /// <param name="skip">The number of messages to skip.</param>
    /// <param name="take">The maximum number of messages to return.</param>
    /// <returns>The messages, newest first.</returns>
    private async Task<IReadOnlyList<MailMessage>> FetchPageAsync(object? node, string? searchText, int skip, int take)
    {
        if (searchText is not null)
        {
            return await _dispatcher.SendAsync(new SearchMessagesQuery(searchText, ScopeAccountId(node), skip, take));
        }

        return node switch
        {
            UnifiedInboxNodeViewModel => await _dispatcher.SendAsync(new GetUnifiedInboxQuery(skip, take)),
            LabelNodeViewModel label => await _dispatcher.SendAsync(new GetMessagesByLabelQuery(label.Label.Id, skip, take)),
            FolderNodeViewModel folder => await _dispatcher.SendAsync(new GetMessagesQuery(folder.Folder.AccountId, folder.Folder.Id, skip, take)),
            _ => [],
        };
    }

    /// <summary>
    /// Creates the list item of a message.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <returns>The new item.</returns>
    private MessageItemViewModel CreateItem(MailMessage message)
    {
        return new MessageItemViewModel(message, SaveAttachmentAsync, _localizer, RememberRemoteImagesAsync, _timeProvider);
    }

    /// <summary>
    /// Replaces the message list, keeping the current selection when the message is still listed.
    /// </summary>
    /// <param name="messages">The new messages.</param>
    private void ReplaceMessages(IReadOnlyList<MailMessage> messages)
    {
        // Items of messages that are still listed are kept (and updated) rather than recreated: replacing the selected
        // item would reload the web view, refetch inline images and drop per-message state on every periodic refresh.
        var existing = new Dictionary<Guid, MessageItemViewModel>();
        foreach (var item in Messages)
        {
            existing[item.Id] = item;
        }

        var target = new List<MessageItemViewModel>(messages.Count);
        foreach (var message in messages)
        {
            if (existing.TryGetValue(message.Id, out var kept))
            {
                kept.Update(message);
                target.Add(kept);
            }
            else
            {
                target.Add(CreateItem(message));
            }
        }

        if (SelectedMessage is { } selected && !target.Contains(selected))
        {
            SelectedMessage = null;
        }

        var keep = target.ToHashSet();
        for (var i = Messages.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Messages[i]))
            {
                Messages.RemoveAt(i);
            }
        }

        for (var i = 0; i < target.Count; i++)
        {
            if (i < Messages.Count && ReferenceEquals(Messages[i], target[i]))
            {
                continue;
            }

            var current = Messages.IndexOf(target[i]);
            if (current >= 0)
            {
                Messages.Move(current, i);
            }
            else
            {
                Messages.Insert(i, target[i]);
            }
        }
    }

    /// <summary>
    /// Gets the account a tree selection belongs to.
    /// </summary>
    /// <param name="node">The selected node.</param>
    /// <returns>The account identifier, or <see langword="null"/> when nothing or a cross-account node (unified inbox, label) is selected.</returns>
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
            _logger.LogError(ex, "{Operation} failed", failureKey);
            StatusText = _localizer.Format("Error_Format", _localizer[failureKey], ex.Message);
        }
    }
}
