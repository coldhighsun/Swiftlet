using System.Globalization;
using Mediator;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Services;
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

    /// <inheritdoc />
    public Task<bool> ShowAddAccountAsync()
    {
        AddAccountCalls++;
        OnAddAccount?.Invoke();
        return Task.FromResult(AddAccountResult);
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
        string? html = null)
    {
        return MailMessage.Create(
            account.Id,
            folder.Id,
            uid,
            subject,
            "Alice",
            "alice@example.com",
            "me@example.com",
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(uid),
            "line one\nline two",
            html,
            $"<{uid}@example.com>",
            isRead);
    }
}
