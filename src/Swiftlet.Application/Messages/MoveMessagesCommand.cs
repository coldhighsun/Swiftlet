using Mediator;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Application.Messages;

/// <summary>
/// Moves messages of one account to another of its folders. They disappear from every view at once; the server is told in
/// the background. The result is the number of messages queued; those already in the target folder are not counted.
/// </summary>
/// <param name="AccountId">The account that owns the messages and the folder.</param>
/// <param name="MessageIds">The cached message identifiers.</param>
/// <param name="TargetFolderId">The folder to move the messages to.</param>
public sealed record MoveMessagesCommand(Guid AccountId, IReadOnlyList<Guid> MessageIds, Guid TargetFolderId) : ICommand<int>;

/// <summary>
/// Handles <see cref="MoveMessagesCommand"/> by marking the messages and waking the background worker it shares with deletion.
/// </summary>
/// <param name="folders">The folder repository.</param>
/// <param name="messages">The message repository.</param>
/// <param name="signal">Wakes the background worker.</param>
public sealed class MoveMessagesCommandHandler(IFolderRepository folders, IMessageRepository messages, DeletionSignal signal)
    : ICommandHandler<MoveMessagesCommand, int>
{
    /// <inheritdoc />
    public async ValueTask<int> Handle(MoveMessagesCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        _ = await folders.GetAsync(command.AccountId, command.TargetFolderId, cancellationToken)
            ?? throw new InvalidOperationException($"Folder {command.TargetFolderId} was not found.");

        var marked = await messages.MarkMovePendingAsync(command.AccountId, command.MessageIds, command.TargetFolderId, cancellationToken);
        signal.Notify();
        return marked;
    }
}
