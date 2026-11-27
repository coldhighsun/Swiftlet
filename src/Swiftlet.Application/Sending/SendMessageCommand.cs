using Mediator;
using Microsoft.Extensions.Logging;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Accounts;
using Swiftlet.Application.Contacts;
using Swiftlet.Application.Logging;
using Swiftlet.Application.Sync;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Contacts;

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
/// <param name="contacts">Remembers the recipients for the address book.</param>
/// <param name="logger">The logger; optional.</param>
public sealed class SendMessageCommandHandler(
    IAccountRepository accounts,
    IMailSender sender,
    SyncEvents events,
    IContactRepository contacts,
    ILogger<SendMessageCommandHandler>? logger = null)
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
        await RememberRecipientsAsync(account.Id, command.Message, cancellationToken);
        return Unit.Value;
    }

    /// <summary>
    /// Remembers who the message went to. The message has been delivered by now, so a failure is only logged: reporting
    /// it would make the user send the message again.
    /// </summary>
    /// <param name="accountId">The sending account.</param>
    /// <param name="message">The delivered message.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the recipients are saved or saving failed.</returns>
    private async Task RememberRecipientsAsync(Guid accountId, OutgoingMessage message, CancellationToken cancellationToken)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var recipients = message.To
                .Concat(message.Cc)
                .Concat(message.Bcc)
                .Select(RecipientList.Parse)
                .Where(r => Contact.IsValidEmail(r.Address))
                .GroupBy(r => r.Address.ToLowerInvariant())
                .Select(g => new SentRecipient(
                    accountId,
                    g.Key,
                    g.Select(r => r.Name).FirstOrDefault(n => n.Length > 0) ?? string.Empty,
                    now))
                .ToList();
            await contacts.RecordSentRecipientsAsync(recipients, cancellationToken);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "The recipients of a sent message could not be remembered");
        }
    }
}
