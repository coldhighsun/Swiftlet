using Mediator;
using Swiftlet.Application.Logging;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Application.Messages;

/// <summary>
/// Deletes every message a folder holds on the server, including history that was never downloaded.
/// Messages that arrive after the request are kept. The server is told in the background.
/// The result is <see langword="false"/> when nothing is cached, so there is nothing to delete.
/// </summary>
/// <param name="AccountId">The account that owns the folder.</param>
/// <param name="FolderId">The folder.</param>
public sealed record EmptyFolderCommand(Guid AccountId, Guid FolderId) : ICommand<bool>, IFolderRequest;

/// <summary>
/// Handles <see cref="EmptyFolderCommand"/>.
/// </summary>
/// <param name="folders">The folder repository.</param>
/// <param name="messages">The message repository.</param>
/// <param name="signal">Wakes the background deletion.</param>
public sealed class EmptyFolderCommandHandler(IFolderRepository folders, IMessageRepository messages, DeletionSignal signal)
    : ICommandHandler<EmptyFolderCommand, bool>
{
    /// <inheritdoc />
    public async ValueTask<bool> Handle(EmptyFolderCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var folder = await folders.GetAsync(command.AccountId, command.FolderId, cancellationToken)
            ?? throw new InvalidOperationException($"Folder {command.FolderId} was not found.");
        if (folder.Role == FolderRole.AllMail)
        {
            // All Mail mirrors every other folder, so emptying it would delete the whole mailbox.
            throw new InvalidOperationException($"Folder '{folder.FullName}' holds all mail of the account and cannot be emptied.");
        }

        // The newest message the user has seen bounds the request, so mail arriving afterwards is never deleted.
        var newest = await messages.GetMaxUidAsync(command.AccountId, folder.Id, cancellationToken);
        if (newest == 0)
        {
            return false;
        }

        folder.RequestEmpty(newest);
        await folders.SaveChangesAsync(cancellationToken);
        await messages.MarkFolderDeletePendingAsync(command.AccountId, folder.Id, newest, cancellationToken);
        signal.Notify();
        return true;
    }
}
