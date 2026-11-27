using System.Globalization;
using Mediator;
using Swiftlet.Application.Sync;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Services;
using Swiftlet.Desktop.Settings;
using Swiftlet.Desktop.ViewModels;
using Swiftlet.Domain.Accounts;
using Swiftlet.Infrastructure.Persistence.Encryption;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Desktop.Tests;

/// <summary>
/// A request dispatcher answering from registered handlers and recording every request.
/// </summary>
internal sealed class FakeDispatcher : IRequestDispatcher
{
    /// <summary>
    /// The registered handlers keyed by request type.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Func<object, CancellationToken, Task<object?>>> _handlers = new();

    /// <summary>
    /// Gets the requests received, in order.
    /// </summary>
    public List<object> Requests { get; } = [];

    /// <summary>
    /// Registers a handler for a request type.
    /// </summary>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <param name="handler">The handler producing the response.</param>
    public void On<TRequest>(Func<TRequest, object?> handler)
    {
        _handlers[typeof(TRequest)] = (request, _) => Task.FromResult(handler((TRequest)request));
    }

    /// <summary>
    /// Registers an asynchronous handler that observes cancellation.
    /// </summary>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <param name="handler">The handler producing the response.</param>
    public void OnAsync<TRequest>(Func<TRequest, CancellationToken, Task<object?>> handler)
    {
        _handlers[typeof(TRequest)] = (request, token) => handler((TRequest)request, token);
    }

    /// <summary>
    /// The registered stream handlers keyed by request type.
    /// </summary>
    private readonly Dictionary<Type, Func<object, CancellationToken, object>> _streams = [];

    /// <summary>
    /// Registers a handler for a streamed query.
    /// </summary>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <typeparam name="TResponse">The item type.</typeparam>
    /// <param name="handler">The handler producing the items.</param>
    public void OnStream<TRequest, TResponse>(Func<TRequest, CancellationToken, IAsyncEnumerable<TResponse>> handler)
    {
        _streams[typeof(TRequest)] = (request, token) => handler((TRequest)request, token);
    }

    /// <inheritdoc />
    public IAsyncEnumerable<TResponse> StreamAsync<TResponse>(
        IStreamQuery<TResponse> query,
        CancellationToken cancellationToken = default)
    {
        lock (Requests)
        {
            Requests.Add(query);
        }

        return _streams.TryGetValue(query.GetType(), out var handler)
            ? (IAsyncEnumerable<TResponse>)handler(query, cancellationToken)
            : throw new InvalidOperationException($"No fake handler for {query.GetType().Name}.");
    }

    /// <summary>
    /// Gets the recorded requests of one type.
    /// </summary>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <returns>The matching requests.</returns>
    public List<TRequest> Sent<TRequest>()
    {
        // Locked because requests are also sent, and recorded, from thread-pool threads.
        lock (Requests)
        {
            return [.. Requests.OfType<TRequest>()];
        }
    }

    /// <inheritdoc />
    public Task<TResponse> SendAsync<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default)
    {
        return Dispatch<TResponse>(command, cancellationToken);
    }

    /// <inheritdoc />
    public Task<TResponse> SendAsync<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default)
    {
        return Dispatch<TResponse>(query, cancellationToken);
    }

    /// <summary>
    /// Records the request and runs its handler.
    /// </summary>
    /// <typeparam name="TResponse">The response type.</typeparam>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The cancellation token passed to the handler.</param>
    /// <returns>The response.</returns>
    private async Task<TResponse> Dispatch<TResponse>(object request, CancellationToken cancellationToken)
    {
        // Locked because folder checks and unread counts are sent from thread-pool threads.
        lock (Requests)
        {
            Requests.Add(request);
        }

        if (!_handlers.TryGetValue(request.GetType(), out var handler))
        {
            throw new InvalidOperationException($"No fake handler for {request.GetType().Name}.");
        }

        return (TResponse)(await handler(request, cancellationToken))!;
    }
}

/// <summary>
/// A clock that only moves when a test tells it to.
/// </summary>
internal sealed class FakeClock : TimeProvider
{
    /// <summary>
    /// The current time.
    /// </summary>
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow()
    {
        return _now;
    }

    /// <inheritdoc />
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    /// <summary>
    /// Moves the clock forward.
    /// </summary>
    /// <param name="by">How far to move.</param>
    public void Advance(TimeSpan by)
    {
        _now += by;
    }
}

/// <summary>
/// A dialog service recording what the view models asked for.
/// </summary>
internal sealed class FakeDialogService : IDialogService
{
    /// <summary>
    /// Gets or sets the result of the add-account dialog.
    /// </summary>
    public bool AddAccountResult { get; set; }

    /// <summary>
    /// Gets or sets an action run when the add-account dialog is shown (for example to make the account appear).
    /// </summary>
    public Action? OnAddAccount { get; set; }

    /// <summary>
    /// Gets the number of times the add-account dialog was shown.
    /// </summary>
    public int AddAccountCalls { get; private set; }

    /// <summary>
    /// Gets or sets the result of the account settings dialog.
    /// </summary>
    public Swiftlet.Application.Accounts.UpdateAccountResult? EditAccountResult { get; set; }

    /// <summary>
    /// Gets or sets an action run when the account settings dialog is shown (for example to change the stored account).
    /// </summary>
    public Action? OnEditAccount { get; set; }

    /// <summary>
    /// Gets the accounts the settings dialog was shown for.
    /// </summary>
    public List<MailAccount> EditedAccounts { get; } = [];

    /// <summary>
    /// Gets the folders passed to the account settings dialog the last time it was shown.
    /// </summary>
    public IReadOnlyList<Swiftlet.Domain.Folders.MailFolder> EditedFolders { get; private set; } = [];

    /// <summary>
    /// Gets the compose requests received.
    /// </summary>
    public List<ComposeRequest> ComposeRequests { get; } = [];

    /// <summary>
    /// Gets or sets the path returned by the save picker.
    /// </summary>
    public string? SavePath { get; set; }

    /// <summary>
    /// Gets the files returned by the file picker.
    /// </summary>
    public List<string> PickedFiles { get; } = [];

    /// <summary>
    /// Gets the HTML bodies shown in the browser.
    /// </summary>
    public List<string> BrowserHtml { get; } = [];

    /// <summary>
    /// Gets or sets the answer returned by the confirmation dialog.
    /// </summary>
    public bool ConfirmResult { get; set; }

    /// <summary>
    /// Gets the confirmation prompts shown, as title, message and confirm button text.
    /// </summary>
    public List<(string Title, string Message, string OkText)> Confirmations { get; } = [];

    /// <summary>
    /// Gets or sets an action run when the label manager is shown (for example to change the stored labels).
    /// </summary>
    public Action? OnLabelManager { get; set; }

    /// <summary>
    /// Gets the number of times the label manager was shown.
    /// </summary>
    public int LabelManagerCalls { get; private set; }

    /// <inheritdoc />
    public Task<bool> ConfirmAsync(string title, string message, string okText)
    {
        Confirmations.Add((title, message, okText));
        return Task.FromResult(ConfirmResult);
    }

    /// <summary>
    /// Gets the number of times the update dialog was shown.
    /// </summary>
    public int UpdateCalls { get; private set; }

    /// <inheritdoc />
    public bool HasUnsavedWorkWindows { get; set; }

    /// <inheritdoc />
    public Task ShowUpdateAsync()
    {
        UpdateCalls++;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> ShowAddAccountAsync()
    {
        AddAccountCalls++;
        OnAddAccount?.Invoke();
        return Task.FromResult(AddAccountResult);
    }

    /// <inheritdoc />
    public Task<Swiftlet.Application.Accounts.UpdateAccountResult?> ShowEditAccountAsync(
        MailAccount account,
        IReadOnlyList<Swiftlet.Domain.Folders.MailFolder> folders)
    {
        EditedAccounts.Add(account);
        EditedFolders = folders;
        OnEditAccount?.Invoke();
        return Task.FromResult(EditAccountResult);
    }

    /// <summary>
    /// Gets the number of times the settings window was shown.
    /// </summary>
    public int SettingsCalls { get; private set; }

    /// <inheritdoc />
    public Task ShowSettingsAsync()
    {
        SettingsCalls++;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ShowLabelManagerAsync()
    {
        LabelManagerCalls++;
        OnLabelManager?.Invoke();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets or sets the recipient the contacts dialog returns.
    /// </summary>
    public string? ContactsRecipient { get; set; }

    /// <summary>
    /// Gets the requests the contacts dialog was shown with.
    /// </summary>
    public List<ContactsRequest?> ContactsRequests { get; } = [];

    /// <summary>
    /// Gets or sets the path returned by the contact file picker.
    /// </summary>
    public string? ContactFile { get; set; }

    /// <inheritdoc />
    public Task<string?> ShowContactsAsync(ContactsRequest? request)
    {
        ContactsRequests.Add(request);
        return Task.FromResult(ContactsRecipient);
    }

    /// <summary>
    /// Gets the messages and labels the classification dialog was shown for.
    /// </summary>
    public List<(IReadOnlyList<MailMessage> Messages, IReadOnlyList<Swiftlet.Domain.Labels.Label> Labels)> ClassifyCalls { get; } = [];

    /// <summary>
    /// Gets or sets an action run when the classification dialog is shown; its result says whether something changed.
    /// </summary>
    public Func<bool>? OnClassify { get; set; }

    /// <inheritdoc />
    public Task<bool> ShowClassifyAsync(
        IReadOnlyList<MailMessage> messages,
        IReadOnlyList<Swiftlet.Domain.Labels.Label> labels)
    {
        ClassifyCalls.Add((messages, labels));
        return Task.FromResult(OnClassify?.Invoke() ?? false);
    }

    /// <summary>
    /// Gets the texts put on the clipboard.
    /// </summary>
    public List<string> CopiedTexts { get; } = [];

    /// <inheritdoc />
    public Task CopyTextAsync(string text)
    {
        CopiedTexts.Add(text);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<string?> PickContactFileAsync()
    {
        return Task.FromResult(ContactFile);
    }

    /// <inheritdoc />
    public Task ShowComposeAsync(IReadOnlyList<MailAccount> accounts, ComposeRequest request)
    {
        ComposeRequests.Add(request);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<string?> PickSavePathAsync(string suggestedFileName)
    {
        return Task.FromResult(SavePath);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> PickFilesAsync()
    {
        return Task.FromResult<IReadOnlyList<string>>(PickedFiles);
    }

    /// <inheritdoc />
    public void ShowHtmlInBrowser(string html)
    {
        BrowserHtml.Add(html);
    }

    /// <summary>
    /// Gets the folders that were asked to be opened.
    /// </summary>
    public List<string> OpenedFolders { get; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether opening a folder works.
    /// </summary>
    public bool OpenFolderResult { get; set; } = true;

    /// <summary>
    /// Gets or sets the exception thrown when a folder is opened.
    /// </summary>
    public Exception? OpenFolderError { get; set; }

    /// <inheritdoc />
    public Task<bool> OpenFolderAsync(string path)
    {
        OpenedFolders.Add(path);
        return OpenFolderError is null ? Task.FromResult(OpenFolderResult) : Task.FromException<bool>(OpenFolderError);
    }
}

/// <summary>
/// An in-memory language preference store.
/// </summary>
internal sealed class FakeLanguageStore : ILanguagePreferenceStore
{
    /// <summary>
    /// Gets or sets the stored preference.
    /// </summary>
    public string Preference { get; set; } = LocalizationManager.System;

    /// <summary>
    /// Gets or sets an exception thrown on save.
    /// </summary>
    public Exception? SaveFailure { get; set; }

    /// <inheritdoc />
    public string Load()
    {
        return Preference;
    }

    /// <inheritdoc />
    public void Save(string preference)
    {
        if (SaveFailure is not null)
        {
            throw SaveFailure;
        }

        Preference = preference;
    }
}

/// <summary>
/// In-memory remote image rules that record their calls.
/// </summary>
internal sealed class FakeRemoteImageRules : Swiftlet.Application.Abstractions.IRemoteImageRules
{
    /// <summary>
    /// The saved rules.
    /// </summary>
    private readonly SortedSet<string> _rules = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets the rules passed to <see cref="AddAsync"/>, including duplicates.
    /// </summary>
    public List<string> AddCalls { get; } = [];

    /// <summary>
    /// Gets the rules passed to <see cref="RemoveAsync"/>.
    /// </summary>
    public List<string> RemoveCalls { get; } = [];

    /// <summary>
    /// Gets or sets an exception thrown by <see cref="AddAsync"/>.
    /// </summary>
    public Exception? AddFailure { get; set; }

    /// <summary>
    /// Gets the saved rules in order.
    /// </summary>
    public IReadOnlyCollection<string> Rules => _rules;

    /// <inheritdoc />
    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public bool IsAllowed(string? fromAddress)
    {
        return Swiftlet.Application.Abstractions.RemoteImageRule.Candidates(fromAddress).Any(_rules.Contains);
    }

    /// <inheritdoc />
    public Task<bool> AddAsync(string rule, CancellationToken cancellationToken = default)
    {
        AddCalls.Add(rule);
        if (AddFailure is not null)
        {
            throw AddFailure;
        }

        return Task.FromResult(_rules.Add(rule));
    }

    /// <inheritdoc />
    public Task<bool> RemoveAsync(string rule, CancellationToken cancellationToken = default)
    {
        RemoveCalls.Add(rule);
        return Task.FromResult(_rules.Remove(rule));
    }

    /// <inheritdoc />
    public (IReadOnlyList<string> Rules, int TotalMatches) Search(string? filter, int take)
    {
        var text = filter?.Trim();
        var matches = string.IsNullOrEmpty(text)
            ? _rules.ToList()
            : _rules.Where(r => r.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
        return (matches.Take(take).ToList(), matches.Count);
    }
}

/// <summary>
/// In-memory application settings.
/// </summary>
internal sealed class FakeAppSettings : IAppSettings
{
    /// <summary>
    /// Gets the values passed to <see cref="SetRemoveServerDeletedMessages"/>.
    /// </summary>
    public List<bool> SetCalls { get; } = [];

    /// <summary>
    /// Gets or sets an exception thrown on save.
    /// </summary>
    public Exception? Failure { get; set; }

    /// <inheritdoc />
    public bool RemoveServerDeletedMessages { get; set; }

    /// <inheritdoc />
    public bool DownloadFullHistory { get; set; } = true;

    /// <inheritdoc />
    public string Theme { get; set; } = ThemePreference.System;

    /// <inheritdoc />
    public bool CloseToTray { get; set; } = true;

    /// <inheritdoc />
    public void SetCloseToTray(bool value)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        CloseToTray = value;
    }

    /// <inheritdoc />
    public bool ShowNewMailNotifications { get; set; } = true;

    /// <inheritdoc />
    public void SetShowNewMailNotifications(bool value)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        ShowNewMailNotifications = value;
    }

    /// <inheritdoc />
    public bool StartMinimized { get; set; }

    /// <inheritdoc />
    public void SetStartMinimized(bool value)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        StartMinimized = value;
    }

    /// <inheritdoc />
    public string TrayUnreadStyle { get; set; } = Swiftlet.Desktop.Settings.TrayUnreadStyle.Dot;

    /// <inheritdoc />
    public event EventHandler? TrayUnreadStyleChanged;

    /// <inheritdoc />
    public void SetTrayUnreadStyle(string value)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        TrayUnreadStyle = value;
        TrayUnreadStyleChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public string ContactNameOrder { get; set; } = Swiftlet.Desktop.Settings.ContactNameOrder.Auto;

    /// <inheritdoc />
    public event EventHandler? ContactNameOrderChanged;

    /// <inheritdoc />
    public void SetContactNameOrder(string value)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        ContactNameOrder = value;
        ContactNameOrderChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public void SetTheme(string value)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        Theme = value;
    }

    /// <inheritdoc />
    public WindowPlacement? MainWindowPlacement { get; set; }

    /// <inheritdoc />
    public void SetMainWindowPlacement(WindowPlacement value)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        MainWindowPlacement = value;
    }

    /// <inheritdoc />
    public bool CheckForUpdates { get; set; } = true;

    /// <inheritdoc />
    public void SetCheckForUpdates(bool value)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        CheckForUpdates = value;
    }

    /// <inheritdoc />
    public UpdateCheckState UpdateCheckState { get; set; } = new();

    /// <inheritdoc />
    public void ChangeUpdateCheckState(Func<UpdateCheckState, UpdateCheckState> change)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        UpdateCheckState = change(UpdateCheckState);
    }

    /// <summary>
    /// Gets the values passed to <see cref="SetDownloadFullHistory"/>.
    /// </summary>
    public List<bool> HistoryCalls { get; } = [];

    /// <inheritdoc />
    public void SetDownloadFullHistory(bool value)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        HistoryCalls.Add(value);
        DownloadFullHistory = value;
    }

    /// <inheritdoc />
    public void SetRemoveServerDeletedMessages(bool value)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        SetCalls.Add(value);
        RemoveServerDeletedMessages = value;
    }
}

/// <summary>
/// Records the themes it was asked to apply.
/// </summary>
internal sealed class FakeThemeApplier : IThemeApplier
{
    /// <summary>
    /// Gets the preferences applied, in order.
    /// </summary>
    public List<string> Applied { get; } = [];

    /// <inheritdoc />
    public void Apply(string preference)
    {
        Applied.Add(preference);
    }
}

/// <summary>
/// Creates localizers with a fixed language, independent of the machine running the tests.
/// </summary>
internal static class TestLocalizer
{
    /// <summary>
    /// Creates a localizer fixed to Simplified Chinese.
    /// </summary>
    /// <returns>The localizer.</returns>
    public static LocalizationManager Chinese()
    {
        return new LocalizationManager(CultureInfo.InvariantCulture, LocalizationManager.SimplifiedChinese);
    }

    /// <summary>
    /// Creates a localizer fixed to English.
    /// </summary>
    /// <returns>The localizer.</returns>
    public static LocalizationManager English()
    {
        return new LocalizationManager(CultureInfo.InvariantCulture, LocalizationManager.English);
    }
}

/// <summary>
/// Helpers for building test data.
/// </summary>
internal static class TestData
{
    /// <summary>
    /// Creates an account.
    /// </summary>
    /// <param name="email">The email address.</param>
    /// <returns>The account.</returns>
    public static MailAccount Account(string email = "me@example.com")
    {
        return MailAccount.Create(
            "Me",
            email,
            AuthMethod.Password,
            new ServerEndpoint("imap.example.com", 993, true),
            new ServerEndpoint("smtp.example.com", 465, true),
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Creates an OAuth 2.0 account.
    /// </summary>
    /// <param name="email">The email address.</param>
    /// <returns>The account.</returns>
    public static MailAccount OAuthAccount(string email = "oauth@example.com")
    {
        return MailAccount.Create(
            "OAuth",
            email,
            AuthMethod.OAuth2,
            new ServerEndpoint("imap.example.com", 993, true),
            new ServerEndpoint("smtp.example.com", 465, true),
            DateTimeOffset.UtcNow,
            OAuthProvider.Google);
    }

    /// <summary>
    /// Creates a folder.
    /// </summary>
    /// <param name="account">The owning account.</param>
    /// <param name="fullName">The full name.</param>
    /// <param name="role">The role.</param>
    /// <returns>The folder.</returns>
    public static MailFolder Folder(MailAccount account, string fullName, FolderRole role)
    {
        return MailFolder.Create(account.Id, fullName, fullName, role);
    }

    /// <summary>
    /// Creates a message.
    /// </summary>
    /// <param name="account">The owning account.</param>
    /// <param name="folder">The containing folder.</param>
    /// <param name="uid">The UID.</param>
    /// <param name="subject">The subject.</param>
    /// <param name="isRead">Whether the message is read.</param>
    /// <param name="html">The HTML body, if any.</param>
    /// <returns>The message.</returns>
    public static MailMessage Message(
        MailAccount account,
        MailFolder folder,
        uint uid,
        string subject = "Hello",
        bool isRead = false,
        string? html = null,
        string fromAddress = "alice@example.com")
    {
        return MailMessage.Create(
            account.Id,
            folder.Id,
            uid,
            subject,
            "Alice",
            fromAddress,
            "me@example.com",
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(uid),
            "line one\nline two",
            html,
            $"<{uid}@example.com>",
            isRead);
    }
}

/// <summary>
/// A sync notification source the tests raise by hand.
/// </summary>
internal sealed class FakeSyncEvents : ISyncEvents
{
    /// <inheritdoc />
    public event EventHandler<DeletionProgressEventArgs>? DeletionProgressed;

    /// <summary>
    /// Raises <see cref="DeletionProgressed"/>.
    /// </summary>
    /// <param name="args">The event data.</param>
    public void RaiseDeletion(DeletionProgressEventArgs args)
    {
        DeletionProgressed?.Invoke(this, args);
    }

    /// <inheritdoc />
    public event EventHandler<AccountSyncedEventArgs>? AccountSynced;

    /// <inheritdoc />
    public event EventHandler<BackfillProgressEventArgs>? BackfillProgressed;

    /// <summary>
    /// Raises <see cref="BackfillProgressed"/> on the calling thread.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="downloaded">The messages downloaded so far.</param>
    /// <param name="finished">Whether the pass is over.</param>
    /// <param name="folderName">The folder being downloaded, if the report is about one.</param>
    /// <param name="remaining">How many older messages the folder still holds.</param>
    public void RaiseProgress(Guid accountId, int downloaded, bool finished, string? folderName = null, int remaining = 0)
    {
        BackfillProgressed?.Invoke(this, new BackfillProgressEventArgs(accountId, downloaded, finished, folderName, remaining));
    }

    /// <summary>
    /// Raises <see cref="AccountSynced"/> on the calling thread.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    public void Raise(Guid accountId)
    {
        AccountSynced?.Invoke(this, new AccountSyncedEventArgs(accountId, 1));
    }

    /// <inheritdoc />
    public event EventHandler<MessageSentEventArgs>? MessageSent;

    /// <summary>
    /// Raises <see cref="MessageSent"/> on the calling thread.
    /// </summary>
    /// <param name="accountId">The sending account.</param>
    public void RaiseSent(Guid accountId)
    {
        MessageSent?.Invoke(this, new MessageSentEventArgs(accountId));
    }

    /// <summary>
    /// Gets a value indicating whether anyone is subscribed.
    /// </summary>
    public bool HasSubscribers => AccountSynced is not null || BackfillProgressed is not null || MessageSent is not null
        || SyncStatusChanged is not null;

    /// <inheritdoc />
    public event EventHandler<SyncStatusChangedEventArgs>? SyncStatusChanged;

    /// <summary>
    /// Raises <see cref="SyncStatusChanged"/> on the calling thread.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    public void RaiseSyncStatus(Guid accountId)
    {
        SyncStatusChanged?.Invoke(this, new SyncStatusChangedEventArgs(accountId));
    }

    /// <inheritdoc />
    public event EventHandler<NewMailEventArgs>? NewMailArrived;

    /// <summary>
    /// Raises <see cref="NewMailArrived"/> on the calling thread.
    /// </summary>
    /// <param name="args">The event data.</param>
    public void RaiseNewMail(NewMailEventArgs args)
    {
        NewMailArrived?.Invoke(this, args);
    }

    /// <summary>
    /// Gets a value indicating whether anyone listens for new mail.
    /// </summary>
    public bool HasNewMailSubscribers => NewMailArrived is not null;
}

/// <summary>
/// Records what the new mail pop-up was asked to do.
/// </summary>
internal sealed class FakeNewMailPresenter : INewMailPresenter
{
    /// <summary>
    /// Gets the contents shown, one entry per call.
    /// </summary>
    public List<NewMailViewModel> Shown { get; } = [];

    /// <summary>
    /// Gets how often the pop-up was removed.
    /// </summary>
    public int Closed { get; private set; }

    /// <summary>
    /// Gets or sets an exception thrown instead of showing.
    /// </summary>
    public Exception? Failure { get; set; }

    /// <inheritdoc />
    public void Show(NewMailViewModel viewModel)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        Shown.Add(viewModel);
    }

    /// <inheritdoc />
    public void Close()
    {
        Closed++;
    }
}

/// <summary>
/// A UI-thread marshaller that runs the action inline and records every call.
/// </summary>
internal sealed class FakeUiThread : IUiThread
{
    /// <summary>
    /// Gets the tasks returned for each marshalled action.
    /// </summary>
    public List<Task> Invocations { get; } = [];

    /// <inheritdoc />
    public Task InvokeAsync(Func<Task> action)
    {
        var task = action();
        lock (Invocations)
        {
            Invocations.Add(task);
        }

        return task;
    }
}

/// <summary>
/// A scriptable <see cref="IDatabaseProtection"/>.
/// </summary>
internal sealed class FakeDatabaseProtection : IDatabaseProtection
{
    /// <summary>
    /// Gets the results returned by preparation, in order; success once they are used up.
    /// </summary>
    public Queue<DatabaseProtectionResult> Results { get; } = new();

    /// <summary>
    /// Gets or sets an action run during preparation, for example to report progress or to fail.
    /// </summary>
    public Action<IProgress<DatabaseConversionProgress>?>? OnPrepare { get; set; }

    /// <summary>
    /// Gets the number of preparations.
    /// </summary>
    public int PrepareCalls { get; private set; }

    /// <summary>
    /// Gets the number of times continuing without encryption was chosen.
    /// </summary>
    public int ContinueCalls { get; private set; }

    /// <summary>
    /// Gets the number of full resets.
    /// </summary>
    public int ResetAllCalls { get; private set; }

    /// <summary>
    /// Gets the accounts that were reset.
    /// </summary>
    public List<Guid> ResetAccounts { get; } = [];

    /// <inheritdoc />
    public DatabaseProtectionStatus Status { get; set; }

    /// <summary>
    /// Gets or sets what the assessment reports.
    /// </summary>
    public DatabaseProtectionAssessment Assessment { get; set; } = new(new DatabaseProtectionResult(DatabaseProtectionProblem.None), 0);

    /// <inheritdoc />
    public Task<DatabaseProtectionAssessment> AssessAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(Assessment);
    }

    /// <inheritdoc />
    public Task<DatabaseProtectionResult> PrepareAsync(
        IProgress<DatabaseConversionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        PrepareCalls++;
        OnPrepare?.Invoke(progress);
        return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : new DatabaseProtectionResult(DatabaseProtectionProblem.None));
    }

    /// <inheritdoc />
    public void ContinueUnencrypted()
    {
        ContinueCalls++;
    }

    /// <inheritdoc />
    public Task ResetAllAsync(CancellationToken cancellationToken = default)
    {
        ResetAllCalls++;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ResetAccountAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        ResetAccounts.Add(accountId);
        return Task.CompletedTask;
    }
}
