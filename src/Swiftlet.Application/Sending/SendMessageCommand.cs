using Mediator;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Accounts;
using Swiftlet.Application.Logging;
using Swiftlet.Application.Sync;
using Swiftlet.Domain.Accounts;

namespace Swiftlet.Application.Sending;

/// <summary>
/// Sends a message from an account.
/// </summary>
/// <param name="AccountId">The sending account.</param>
/// <param name="Message">The message to send.</param>
/// <param name="SaveToSent">
/// Whether to append a copy to Sent; <see langword="null"/> decides from the provider
/// (skipped for providers that store the copy themselves).
/// </param>
public sealed record SendMessageCommand(Guid AccountId, OutgoingMessage Message, bool? SaveToSent = null) : ICommand<Unit>, IAccountRequest;

/// <summary>
/// Handles <see cref="SendMessageCommand"/>.
/// </summary>
/// <param name="accounts">The account repository.</param>
/// <param name="sender">The mail sender.</param>
/// <param name="events">Receives the notification when a message was delivered.</param>
public sealed class SendMessageCommandHandler(IAccountRepository accounts, IMailSender sender, SyncEvents events)
    : ICommandHandler<SendMessageCommand, Unit>
{
    /// <inheritdoc />
    public async ValueTask<Unit> Handle(SendMessageCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var account = await accounts.GetAsync(command.AccountId, cancellationToken)
            ?? throw new InvalidOperationException($"Account {command.AccountId} was not found.");

        var saveToSent = command.SaveToSent ?? !ProviderPresets.SavesSentCopyAutomatically(account);

        await sender.SendAsync(account, command.Message, saveToSent, cancellationToken);
        events.RaiseMessageSent(account.Id);
        return Unit.Value;
    }
}
