using Mediator;
using Swiftlet.Domain.Labels;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Application.Labels;

/// <summary>
/// Creates a label.
/// </summary>
/// <param name="Name">The label name.</param>
/// <param name="Color">The color as <c>#RRGGBB</c>.</param>
public sealed record CreateLabelCommand(string Name, string Color) : ICommand<Guid>;

/// <summary>
/// Handles <see cref="CreateLabelCommand"/>.
/// </summary>
/// <param name="labels">The label repository.</param>
public sealed class CreateLabelCommandHandler(ILabelRepository labels) : ICommandHandler<CreateLabelCommand, Guid>
{
    /// <inheritdoc />
    public async ValueTask<Guid> Handle(CreateLabelCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var label = Label.Create(command.Name, command.Color);
        if (await labels.NameExistsAsync(label.Name, null, cancellationToken))
        {
            throw new InvalidOperationException($"A label named '{label.Name}' already exists.");
        }

        await labels.AddAsync(label, cancellationToken);
        return label.Id;
    }
}

/// <summary>
/// Renames and recolors a label.
/// </summary>
/// <param name="LabelId">The label identifier.</param>
/// <param name="Name">The new name.</param>
/// <param name="Color">The new color as <c>#RRGGBB</c>.</param>
public sealed record UpdateLabelCommand(Guid LabelId, string Name, string Color) : ICommand<Unit>;

/// <summary>
/// Handles <see cref="UpdateLabelCommand"/>.
/// </summary>
/// <param name="labels">The label repository.</param>
public sealed class UpdateLabelCommandHandler(ILabelRepository labels) : ICommandHandler<UpdateLabelCommand, Unit>
{
    /// <inheritdoc />
    public async ValueTask<Unit> Handle(UpdateLabelCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var label = await labels.GetAsync(command.LabelId, cancellationToken)
            ?? throw new InvalidOperationException($"Label {command.LabelId} was not found.");
        if (await labels.NameExistsAsync(command.Name, label.Id, cancellationToken))
        {
            throw new InvalidOperationException($"A label named '{command.Name.Trim()}' already exists.");
        }

        label.Rename(command.Name);
        label.SetColor(command.Color);
        await labels.SaveChangesAsync(cancellationToken);
        return Unit.Value;
    }
}

/// <summary>
/// Deletes a label. Messages that carried it are kept.
/// </summary>
/// <param name="LabelId">The label identifier.</param>
public sealed record DeleteLabelCommand(Guid LabelId) : ICommand<Unit>;

/// <summary>
/// Handles <see cref="DeleteLabelCommand"/>.
/// </summary>
/// <param name="labels">The label repository.</param>
public sealed class DeleteLabelCommandHandler(ILabelRepository labels) : ICommandHandler<DeleteLabelCommand, Unit>
{
    /// <inheritdoc />
    public async ValueTask<Unit> Handle(DeleteLabelCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        await labels.DeleteAsync(command.LabelId, cancellationToken);
        return Unit.Value;
    }
}

/// <summary>
/// Attaches a label to a message or removes it. The state is local.
/// </summary>
/// <param name="AccountId">The account that owns the message.</param>
/// <param name="MessageId">The message identifier.</param>
/// <param name="LabelId">The label identifier.</param>
/// <param name="Assigned">Whether the label should be attached.</param>
public sealed record SetMessageLabelCommand(Guid AccountId, Guid MessageId, Guid LabelId, bool Assigned) : ICommand<Unit>;

/// <summary>
/// Handles <see cref="SetMessageLabelCommand"/>.
/// </summary>
/// <param name="messages">The message repository.</param>
/// <param name="labels">The label repository.</param>
public sealed class SetMessageLabelCommandHandler(IMessageRepository messages, ILabelRepository labels)
    : ICommandHandler<SetMessageLabelCommand, Unit>
{
    /// <inheritdoc />
    public async ValueTask<Unit> Handle(SetMessageLabelCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var message = await messages.GetAsync(command.AccountId, command.MessageId, cancellationToken)
            ?? throw new InvalidOperationException($"Message {command.MessageId} was not found.");
        if (command.Assigned)
        {
            var label = await labels.GetAsync(command.LabelId, cancellationToken)
                ?? throw new InvalidOperationException($"Label {command.LabelId} was not found.");
            message.AddLabel(label);
        }
        else
        {
            message.RemoveLabel(command.LabelId);
        }

        await messages.SaveLabelsAsync(message, cancellationToken);
        return Unit.Value;
    }
}
