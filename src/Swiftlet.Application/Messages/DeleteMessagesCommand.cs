using Mediator;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Application.Messages;

/// <summary>
/// Identifies a cached message.
/// </summary>
/// <param name="AccountId">The account that owns the message.</param>
/// <param name="MessageId">The cached message identifier.</param>
public sealed record MessageKey(Guid AccountId, Guid MessageId);

/// <summary>
/// Deletes messages. They disappear from every view at once; the server is told in the background.
/// The result is the number of messages queued for deletion.
/// </summary>
/// <param name="Messages">The messages, which may belong to several accounts.</param>
public sealed record DeleteMessagesCommand(IReadOnlyList<MessageKey> Messages) : ICommand<int>;

/// <summary>
/// Handles <see cref="DeleteMessagesCommand"/> by marking the messages and waking the background deletion.
/// </summary>
/// <param name="messages">The message repository.</param>
/// <param name="signal">Wakes the background deletion.</param>
public sealed class DeleteMessagesCommandHandler(IMessageRepository messages, DeletionSignal signal)
    : ICommandHandler<DeleteMessagesCommand, int>
{
    /// <inheritdoc />
    public async ValueTask<int> Handle(DeleteMessagesCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var marked = 0;
        foreach (var account in command.Messages.GroupBy(m => m.AccountId))
        {
            marked += await messages.MarkDeletePendingAsync(account.Key, [.. account.Select(m => m.MessageId)], cancellationToken);
        }

        signal.Notify();
        return marked;
    }
}
