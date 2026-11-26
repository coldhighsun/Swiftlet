using System.Globalization;
using Mediator;
using Swiftlet.Application.Sync;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Services;
using Swiftlet.Desktop.Settings;
using Swiftlet.Domain.Accounts;
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
    private readonly Dictionary<Type, Func<object, CancellationToken, Task<object?>>> _handlers = [];

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
    /// Gets the recorded requests of one type.
    /// </summary>
    /// <typeparam name="TRequest">The request type.</typeparam>
    /// <returns>The matching requests.</returns>
    public List<TRequest> Sent<TRequest>()
    {
        return [.. Requests.OfType<TRequest>()];
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
        Requests.Add(request);
        if (!_handlers.TryGetValue(request.GetType(), out var handler))
        {
            throw new InvalidOperationException($"No fake handler for {request.GetType().Name}.");
        }

        return (TResponse)(await handler(request, cancellationToken))!;
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

    /// <inheritdoc />
    public Task<bool> ShowAddAccountAsync()
    {
        AddAccountCalls++;
        OnAddAccount?.Invoke();
        return Task.FromResult(AddAccountResult);
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
    public bool DownloadAllMail { get; set; } = true;

    /// <summary>
    /// Gets the values passed to <see cref="SetDownloadAllMail"/>.
    /// </summary>
    public List<bool> AllMailCalls { get; } = [];

    /// <inheritdoc />
    public void SetDownloadAllMail(bool value)
    {
        if (Failure is not null)
        {
            throw Failure;
        }

        AllMailCalls.Add(value);
        DownloadAllMail = value;
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
    public event EventHandler<AccountSyncedEventArgs>? AccountSynced;

    /// <inheritdoc />
    public event EventHandler<BackfillProgressEventArgs>? BackfillProgressed;

    /// <summary>
    /// Raises <see cref="BackfillProgressed"/> on the calling thread.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="downloaded">The messages downloaded so far.</param>
    /// <param name="finished">Whether the pass is over.</param>
    public void RaiseProgress(Guid accountId, int downloaded, bool finished)
    {
        BackfillProgressed?.Invoke(this, new BackfillProgressEventArgs(accountId, downloaded, finished));
    }

    /// <summary>
    /// Raises <see cref="AccountSynced"/> on the calling thread.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    public void Raise(Guid accountId)
    {
        AccountSynced?.Invoke(this, new AccountSyncedEventArgs(accountId, 1));
    }

    /// <summary>
    /// Gets a value indicating whether anyone is subscribed.
    /// </summary>
    public bool HasSubscribers => AccountSynced is not null && BackfillProgressed is not null;
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
