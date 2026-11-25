using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Swiftlet.Application.Abstractions;
using Swiftlet.Domain.Accounts;

namespace Swiftlet.Infrastructure.Mail;

/// <summary>
/// Sends messages with MailKit's SMTP client and optionally archives a copy in the Sent folder.
/// </summary>
/// <param name="secrets">The secret store holding account passwords.</param>
/// <param name="imapFactory">The IMAP connection factory used to archive sent copies.</param>
/// <param name="logger">The logger.</param>
internal sealed class MailKitMailSender(
    ISecretStore secrets,
    IImapSessionFactory imapFactory,
    ILogger<MailKitMailSender> logger) : IMailSender
{
    /// <summary>
    /// The network timeout for SMTP operations.
    /// </summary>
    private static readonly TimeSpan NetworkTimeout = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public async Task SendAsync(
        MailAccount account,
        OutgoingMessage message,
        bool saveToSent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(message);
        if (account.AuthMethod != AuthMethod.Password)
        {
            throw new NotSupportedException("OAuth2 authentication is not implemented yet.");
        }

        var mime = OutgoingMimeBuilder.Build(account, message);
        var password = await secrets.GetAsync(AccountSecrets.PasswordKey(account.Id), cancellationToken)
            ?? throw new InvalidOperationException($"No password is stored for account {account.EmailAddress}.");

        using (var smtp = new SmtpClient { Timeout = (int)NetworkTimeout.TotalMilliseconds })
        {
            var options = account.Smtp.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
            await smtp.ConnectAsync(account.Smtp.Host, account.Smtp.Port, options, cancellationToken);
            await smtp.AuthenticateAsync(account.EmailAddress, password, cancellationToken);
            await smtp.SendAsync(mime, cancellationToken);
            await smtp.DisconnectAsync(true, cancellationToken);
        }

        if (!saveToSent)
        {
            return;
        }

        try
        {
            await using var session = await imapFactory.ConnectAsync(account, cancellationToken);
            using var buffer = new MemoryStream();
            await mime.WriteToAsync(buffer, cancellationToken);
            if (!await session.AppendToSentAsync(buffer.ToArray(), cancellationToken))
            {
                logger.LogWarning("No Sent folder found for account {Account}; sent copy not saved.", account.EmailAddress);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The message has already been delivered; failing to archive it must not be reported as a send failure.
            logger.LogWarning(ex, "Message was sent but saving a copy to Sent failed for account {Account}.", account.EmailAddress);
        }
    }
}
