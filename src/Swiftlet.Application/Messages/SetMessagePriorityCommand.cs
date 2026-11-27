using Mediator;
using Swiftlet.Application.Logging;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Application.Messages;

/// <summary>
/// Sets or clears the priority of a message. The state is local.
/// </summary>
/// <param name="AccountId">The account that owns the message.</param>
/// <param name="MessageId">The message identifier.</param>
/// <param name="Priority">The priority, or <see langword="null"/> to clear it.</param>
public sealed record SetMessagePriorityCommand(Guid AccountId, Guid MessageId, MessagePriority? Priority)
    : ICommand<Unit>, IAccountRequest, ILogDetails
{
    /// <inheritdoc />
    public string DescribeForLog(LogNames names)
    {
        return $"message {MessageId}, priority {Priority?.ToString() ?? "none"}";
    }
}

/// <summary>
/// Handles <see cref="SetMessagePriorityCommand"/>.
/// </summary>
/// <param name="messages">The message repository.</param>
public sealed class SetMessagePriorityCommandHandler(IMessageRepository messages)
    : ICommandHandler<SetMessagePriorityCommand, Unit>
{
    /// <inheritdoc />
    public async ValueTask<Unit> Handle(SetMessagePriorityCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await messages.SetPriorityAsync(command.AccountId, command.MessageId, command.Priority, cancellationToken);
        return Unit.Value;
    }
}
