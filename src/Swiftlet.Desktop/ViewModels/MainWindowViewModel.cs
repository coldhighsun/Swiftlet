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
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Labels;
using Swiftlet.Domain.Messages;
using AccountCommands = Swiftlet.Application.Accounts;

namespace Swiftlet.Desktop.ViewModels;

/// <summary>
/// The view model of the main three-pane window: folder tree, message list and reading pane.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
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
    /// The labels last loaded, offered in the per-message label menu.
    /// </summary>
    private IReadOnlyList<Label> _labels = [];

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
    /// <param name="languageSelector">The language picker view model.</param>
    /// <param name="syncEvents">The sync notifications that trigger a refresh.</param>
    /// <param name="uiThread">The UI thread marshaller.</param>
    /// <param name="remoteImageRules">The saved rules that allow remote images for senders.</param>
    /// <param name="logger">The logger; optional.</param>
    public MainWindowViewModel(
        IRequestDispatcher dispatcher,
        IDialogService dialogs,
        ILocalizer localizer,
        LanguageSelectorViewModel languageSelector,
        ISyncEvents syncEvents,
        IUiThread uiThread,
        IRemoteImageRules remoteImageRules,
        ILogger<MainWindowViewModel>? logger = null)
    {
        _logger = logger ?? NullLogger<MainWindowViewModel>.Instance;
        _remoteImageRules = remoteImageRules;
        _syncEvents = syncEvents;
        _uiThread = uiThread;
        _dispatcher = dispatcher;
        _dialogs = dialogs;
        _localizer = localizer;
        LanguageSelector = languageSelector;
        SearchText = string.Empty;
        StatusText = string.Empty;
        UnifiedInbox = new UnifiedInboxNodeViewModel(localizer);
        TreeRoots.Add(UnifiedInbox);
        localizer.LanguageChanged += (_, _) => RefreshLanguage();
        _syncEvents.AccountSynced += OnAccountSynced;
        _syncEvents.BackfillProgressed += OnBackfillProgressed;
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
            StatusText = e.Finished
                ? _localizer.Format("Status_HistoryDone", e.Downloaded)
                : _localizer.Format("Status_HistoryDownloading", e.Downloaded);
            return Task.CompletedTask;
        }));
    }

    /// <summary>
    /// Schedules a coalesced refresh on the UI thread; may be called from any thread.
    /// </summary>
    /// <param name="sender">The event sender.</param>
    /// <param name="e">The event arguments.</param>
    private void OnAccountSynced(object? sender, AccountSyncedEventArgs e)
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
    /// Gets the language picker.
    /// </summary>
    public LanguageSelectorViewModel LanguageSelector { get; }

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

        _logger.LogInformation("Searching ({Length} characters)", text.Length);
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
    /// Deletes the selected message on the mail server after the user confirmed, then removes it from the list.
    /// </summary>
    /// <returns>A task that completes when the deletion has finished or been cancelled.</returns>
    [RelayCommand(CanExecute = nameof(HasSelectedMessage))]
    private async Task DeleteSelectedAsync()
    {
        var item = SelectedMessage!;
        var text = WillDeletePermanently(item.Message)
            ? _localizer.Format("Confirm_Delete_Permanent", item.Subject)
            : _localizer.Format("Confirm_Delete_ToTrash", item.Subject);
        if (!await _dialogs.ConfirmAsync(
                _localizer["Confirm_Delete_Title"],
                text,
                _localizer["Toolbar_Delete"]))
        {
            return;
        }

        await GuardedAsync("Error_DeleteMessage", async () =>
        {
            var outcome = await _dispatcher.SendAsync(new DeleteMessageCommand(item.Message.AccountId, item.Id));
            if (SelectedMessage == item)
            {
                SelectedMessage = null;
            }

            Messages.Remove(item);
            StatusText = outcome == DeleteOutcome.MovedToTrash
                ? _localizer["Status_MovedToTrash"]
                : _localizer["Status_DeletedPermanently"];
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
        IsBusy = true;
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
            IsBusy = false;
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
        return inTrash || !folders.Any(f => f.Folder.Role == FolderRole.Trash);
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
                catch (OAuthReauthenticationRequiredException)
                {
                    // Not a transient failure: the stored sign-in is gone or revoked, so point the user at the fix.
                    problems.Add(_localizer.Format("Status_ReauthenticationRequired", account.EmailAddress));
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

        if (SelectedNode is null)
        {
            SelectedNode = Accounts
                .SelectMany(a => a.Folders)
                .OrderBy(f => f.SortRank)
                .FirstOrDefault();
        }
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

        var added = labels.Select(l => new LabelNodeViewModel(l)).ToList();
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
    /// <returns>A task that completes when the list has been replaced.</returns>
    private async Task LoadMessagesAsync(object? node)
    {
        var version = ++_loadVersion;
        if (node is not (FolderNodeViewModel or UnifiedInboxNodeViewModel or LabelNodeViewModel))
        {
            ReplaceMessages([]);
            return;
        }

        await GuardedAsync("Error_LoadMessages", async () =>
        {
            var messages = node switch
            {
                UnifiedInboxNodeViewModel => await _dispatcher.SendAsync(new GetUnifiedInboxQuery(0, PageSize)),
                LabelNodeViewModel label => await _dispatcher.SendAsync(new GetMessagesByLabelQuery(label.Label.Id, 0, PageSize)),
                _ => await _dispatcher.SendAsync(new GetMessagesQuery(((FolderNodeViewModel)node).Folder.AccountId, ((FolderNodeViewModel)node).Folder.Id, 0, PageSize)),
            };
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
                target.Add(new MessageItemViewModel(message, SaveAttachmentAsync, _localizer, RememberRemoteImagesAsync));
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
