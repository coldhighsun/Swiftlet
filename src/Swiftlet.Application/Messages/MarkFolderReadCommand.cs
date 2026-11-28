using Mediator;
using Swiftlet.Application.Logging;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Application.Messages;

/// <summary>
/// Marks every downloaded unread message of a folder as read. The change is applied locally at once and written to the
/// server in the background; messages that were never downloaded are not touched.
/// The result is the number of messages that changed.
/// </summary>
/// <param name="AccountId">The account that owns the folder.</param>
/// <param name="FolderId">The folder.</param>
public sealed record MarkFolderReadCommand(Guid AccountId, Guid FolderId) : ICommand<int>, IFolderRequest;

/// <summary>
/// Handles <see cref="MarkFolderReadCommand"/>.
/// </summary>
/// <param name="messages">The message repository.</param>
/// <param name="signal">Wakes the background flag push; optional.</param>
public sealed class MarkFolderReadCommandHandler(IMessageRepository messages, FlagPushSignal? signal = null)
    : ICommandHandler<MarkFolderReadCommand, int>
{
    /// <inheritdoc />
    public async ValueTask<int> Handle(MarkFolderReadCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var marked = await messages.MarkFolderReadAsync(command.AccountId, command.FolderId, cancellationToken);
        if (marked > 0)
        {
            signal?.Notify(command.AccountId);
        }

        return marked;
    }
}
