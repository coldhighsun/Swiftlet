using Swiftlet.Application.Accounts;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;

namespace Swiftlet.Desktop.Services;

/// <summary>
/// The kind of compose window to open.
/// </summary>
public enum ComposeMode
{
    /// <summary>
    /// A new, empty message.
    /// </summary>
    New = 0,

    /// <summary>
    /// A reply to a message.
    /// </summary>
    Reply = 1,

    /// <summary>
    /// A forward of a message.
    /// </summary>
    Forward = 2,
}

/// <summary>
/// Values used to pre-fill a compose window.
/// </summary>
/// <param name="Mode">The compose mode.</param>
/// <param name="AccountId">The account to send from by default, if known.</param>
/// <param name="To">The initial recipient list.</param>
/// <param name="Subject">The initial subject.</param>
/// <param name="Body">The initial body.</param>
/// <param name="InReplyTo">The Message-ID being replied to, if any.</param>
/// <param name="Quote">The quoted or forwarded part at the end of the body, if any.</param>
/// <param name="SourceMessageId">The message being replied to, owned by <paramref name="AccountId"/>, if any.</param>
public sealed record ComposeRequest(
    ComposeMode Mode,
    Guid? AccountId,
    string To,
    string Subject,
    string Body,
    string? InReplyTo,
    string Quote = "",
    Guid? SourceMessageId = null);

/// <summary>
/// An address the contacts window is opened for.
/// </summary>
/// <param name="Name">The name that came with the address; may be empty.</param>
/// <param name="Address">The address.</param>
public sealed record ContactsRequest(string Name, string Address);

/// <summary>
/// Windows and pickers the view models need, abstracted so they can be tested without a UI.
/// </summary>
public interface IDialogService
{
    /// <summary>
    /// Asks the user to confirm an action.
    /// </summary>
    /// <param name="title">The dialog title.</param>
    /// <param name="message">The question shown to the user.</param>
    /// <param name="okText">The text of the confirming button.</param>
    /// <returns><see langword="true"/> if the user confirmed.</returns>
    Task<bool> ConfirmAsync(string title, string message, string okText);

    /// <summary>
    /// Shows the add-account dialog.
    /// </summary>
    /// <returns><see langword="true"/> if an account was added.</returns>
    Task<bool> ShowAddAccountAsync();

    /// <summary>
    /// Shows the settings dialog of an account.
    /// </summary>
    /// <param name="account">The account to edit.</param>
    /// <param name="folders">The cached folders of the account.</param>
    /// <returns>What was saved, or <see langword="null"/> if the dialog was cancelled.</returns>
    Task<UpdateAccountResult?> ShowEditAccountAsync(MailAccount account, IReadOnlyList<MailFolder> folders);

    /// <summary>
    /// Shows the settings dialog.
    /// </summary>
    /// <returns>A task that completes when the dialog has been closed.</returns>
    Task ShowSettingsAsync();

    /// <summary>
    /// Gets a value indicating whether a window is open whose content would be lost if the application quit (writing mail, contacts).
    /// </summary>
    bool HasUnsavedWorkWindows { get; }

    /// <summary>
    /// Shows the window offering the newer version that was found, or brings the open one to the front.
    /// </summary>
    /// <returns>A task that completes when the window has closed.</returns>
    Task ShowUpdateAsync();

    /// <summary>
    /// Shows the label manager dialog.
    /// </summary>
    /// <returns>A task that completes when the dialog has been closed.</returns>
    Task ShowLabelManagerAsync();

    /// <summary>
    /// Shows the contacts dialog.
    /// </summary>
    /// <param name="request">An address to show or add, or <see langword="null"/>.</param>
    /// <returns>The recipient the user chose to write to, or <see langword="null"/>.</returns>
    Task<string?> ShowContactsAsync(ContactsRequest? request);

    /// <summary>
    /// Asks the user to pick a contact file (vCard or CSV) to import.
    /// </summary>
    /// <returns>The chosen path, or <see langword="null"/> if cancelled.</returns>
    Task<string?> PickContactFileAsync();

    /// <summary>
    /// Opens a compose window.
    /// </summary>
    /// <param name="accounts">The accounts that can send.</param>
    /// <param name="request">The initial values.</param>
    /// <returns>A task that completes when the window has been opened.</returns>
    Task ShowComposeAsync(IReadOnlyList<MailAccount> accounts, ComposeRequest request);

    /// <summary>
    /// Asks the user where to save a file.
    /// </summary>
    /// <param name="suggestedFileName">The suggested file name.</param>
    /// <returns>The chosen path, or <see langword="null"/> if cancelled.</returns>
    Task<string?> PickSavePathAsync(string suggestedFileName);

    /// <summary>
    /// Asks the user to pick files to attach.
    /// </summary>
    /// <returns>The chosen paths; empty if cancelled.</returns>
    Task<IReadOnlyList<string>> PickFilesAsync();

    /// <summary>
    /// Shows an HTML message body in the system browser with remote content blocked.
    /// </summary>
    /// <param name="html">The HTML body.</param>
    void ShowHtmlInBrowser(string html);

    /// <summary>
    /// Puts a text on the clipboard.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>A task that completes when the text has been copied.</returns>
    Task CopyTextAsync(string text);

    /// <summary>
    /// Opens a folder in the file manager of the system.
    /// </summary>
    /// <param name="path">The folder.</param>
    /// <returns><see langword="true"/> if the system accepted the request.</returns>
    Task<bool> OpenFolderAsync(string path);
}
