using Mediator;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Application.Messages;

/// <summary>
/// Marks a cached message as read or unread. The state is local; it is not written back to the server.
/// </summary>
/// <param name="MessageId">The message identifier.</param>
/// <param name="IsRead">The new read state.</param>
public sealed record SetMessageReadCommand(Guid MessageId, bool IsRead) : ICommand<Unit>;

/// <summary>
/// Handles <see cref="SetMessageReadCommand"/>.
/// </summary>
/// <param name="messages">The message repository.</param>
public sealed class SetMessageReadCommandHandler(IMessageRepository messages) : ICommandHandler<SetMessageReadCommand, Unit>
{
    /// <inheritdoc />
    public async ValueTask<Unit> Handle(SetMessageReadCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var message = await messages.GetAsync(command.MessageId, cancellationToken)
            ?? throw new InvalidOperationException($"Message {command.MessageId} was not found.");
        message.SetRead(command.IsRead);
        await messages.SaveChangesAsync(cancellationToken);
        return Unit.Value;
    }
}

/// <summary>
/// Flags or unflags (stars) a cached message. The state is local; it is not written back to the server.
/// </summary>
/// <param name="MessageId">The message identifier.</param>
/// <param name="IsFlagged">The new flagged state.</param>
public sealed record SetMessageFlaggedCommand(Guid MessageId, bool IsFlagged) : ICommand<Unit>;

/// <summary>
/// Handles <see cref="SetMessageFlaggedCommand"/>.
/// </summary>
/// <param name="messages">The message repository.</param>
public sealed class SetMessageFlaggedCommandHandler(IMessageRepository messages)
    : ICommandHandler<SetMessageFlaggedCommand, Unit>
{
    /// <inheritdoc />
    public async ValueTask<Unit> Handle(SetMessageFlaggedCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var message = await messages.GetAsync(command.MessageId, cancellationToken)
            ?? throw new InvalidOperationException($"Message {command.MessageId} was not found.");
        message.SetFlagged(command.IsFlagged);
        await messages.SaveChangesAsync(cancellationToken);
        return Unit.Value;
    }
}
