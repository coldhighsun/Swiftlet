using Mediator;
using Swiftlet.Application.Abstractions;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Application.Messages;

/// <summary>
/// How a message was removed from the server.
/// </summary>
public enum DeleteOutcome
{
    /// <summary>
    /// The message was moved to the account's Trash folder.
    /// </summary>
    MovedToTrash,

    /// <summary>
    /// The message was permanently deleted.
    /// </summary>
    DeletedPermanently,
}

/// <summary>
/// Deletes a message on the IMAP server and then removes the cached copy.
/// </summary>
/// <param name="MessageId">The cached message identifier.</param>
public sealed record DeleteMessageCommand(Guid MessageId) : ICommand<DeleteOutcome>;

/// <summary>
/// Handles <see cref="DeleteMessageCommand"/>.
/// </summary>
/// <param name="messages">The message repository.</param>
/// <param name="folders">The folder repository.</param>
/// <param name="accounts">The account repository.</param>
/// <param name="imapFactory">The IMAP connection factory.</param>
public sealed class DeleteMessageCommandHandler(
    IMessageRepository messages,
    IFolderRepository folders,
    IAccountRepository accounts,
    IImapSessionFactory imapFactory) : ICommandHandler<DeleteMessageCommand, DeleteOutcome>
{
    /// <inheritdoc />
    public async ValueTask<DeleteOutcome> Handle(DeleteMessageCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var message = await messages.GetAsync(command.MessageId, cancellationToken)
            ?? throw new InvalidOperationException($"Message {command.MessageId} was not found.");
        var folder = await folders.GetAsync(message.FolderId, cancellationToken)
            ?? throw new InvalidOperationException($"Folder {message.FolderId} was not found.");
        var account = await accounts.GetAsync(message.AccountId, cancellationToken)
            ?? throw new InvalidOperationException($"Account {message.AccountId} was not found.");

        // Deleting from Trash, or from an account without Trash, is permanent.
        var trash = (await folders.GetByAccountAsync(account.Id, cancellationToken))
            .FirstOrDefault(f => f.Role == FolderRole.Trash);
        var trashFullName = folder.Role == FolderRole.Trash ? null : trash?.FullName;

        await using (var session = await imapFactory.ConnectAsync(account, cancellationToken))
        {
            // UIDs are only meaningful within one UIDVALIDITY epoch; a stale UID could delete a different message.
            if (folder.UidValidity is { } cachedValidity
                && await session.GetUidValidityAsync(folder.FullName, cancellationToken) != cachedValidity)
            {
                throw new InvalidOperationException(
                    $"The UIDs cached for folder '{folder.FullName}' are stale (UIDVALIDITY changed on the server). Synchronize the account first.");
            }

            await session.DeleteMessageAsync(folder.FullName, message.Uid, trashFullName, cancellationToken);
        }

        // Only after the server confirmed: a failure above leaves the local copy untouched.
        await messages.DeleteAsync(message.Id, cancellationToken);
        return trashFullName is null ? DeleteOutcome.DeletedPermanently : DeleteOutcome.MovedToTrash;
    }
}
