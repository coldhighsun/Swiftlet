using Mediator;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Accounts;
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
public sealed record SendMessageCommand(Guid AccountId, OutgoingMessage Message, bool? SaveToSent = null) : ICommand<Unit>;

/// <summary>
/// Handles <see cref="SendMessageCommand"/>.
/// </summary>
/// <param name="accounts">The account repository.</param>
/// <param name="sender">The mail sender.</param>
public sealed class SendMessageCommandHandler(IAccountRepository accounts, IMailSender sender)
    : ICommandHandler<SendMessageCommand, Unit>
{
    /// <inheritdoc />
    public async ValueTask<Unit> Handle(SendMessageCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var account = await accounts.GetAsync(command.AccountId, cancellationToken)
            ?? throw new InvalidOperationException($"Account {command.AccountId} was not found.");

        var saveToSent = command.SaveToSent
            ?? !(ProviderPresets.TryGet(account.EmailAddress, out var preset) && preset.SavesSentCopyAutomatically);

        await sender.SendAsync(account, command.Message, saveToSent, cancellationToken);
        return Unit.Value;
    }
}
