using Swiftlet.Domain.Accounts;

namespace Swiftlet.Application.Abstractions;

/// <summary>
/// Sends messages through the SMTP server of an account.
/// </summary>
public interface IMailSender
{
    /// <summary>
    /// Sends a message.
    /// </summary>
    /// <param name="account">The sending account.</param>
    /// <param name="message">The message to send.</param>
    /// <param name="saveToSent">Whether to append a copy to the Sent folder over IMAP after sending.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the message has been accepted by the server.</returns>
    Task SendAsync(
        MailAccount account,
        OutgoingMessage message,
        bool saveToSent,
        CancellationToken cancellationToken = default);
}
