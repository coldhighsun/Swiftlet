using MimeKit;
using Swiftlet.Application.Abstractions;

namespace Swiftlet.Infrastructure.Mail;

/// <summary>
/// Maps MimeKit messages to the application's <see cref="RemoteMessage"/> model.
/// </summary>
internal static class MimeMessageMapper
{
    /// <summary>
    /// The sender address used when a message has no usable From header.
    /// </summary>
    private const string UnknownSender = "unknown@invalid";

    /// <summary>
    /// Maps a downloaded message.
    /// </summary>
    /// <param name="uid">The IMAP UID.</param>
    /// <param name="message">The parsed message.</param>
    /// <param name="isRead">Whether the server flags the message as seen.</param>
    /// <param name="fallbackDate">The date to use when the Date header is missing.</param>
    /// <returns>The mapped message.</returns>
    public static RemoteMessage Map(uint uid, MimeMessage message, bool isRead, DateTimeOffset fallbackDate)
    {
        ArgumentNullException.ThrowIfNull(message);

        var from = message.From.Mailboxes.FirstOrDefault();
        var recipients = message.To.Mailboxes
            .Concat(message.Cc.Mailboxes)
            .Select(m => m.Address)
            .Where(a => !string.IsNullOrWhiteSpace(a));

        var htmlBody = message.HtmlBody;
        var textBody = message.TextBody;
        if (string.IsNullOrWhiteSpace(textBody))
        {
            textBody = HtmlToText.Convert(htmlBody);
        }

        var attachments = message.Attachments
            .OfType<MimePart>()
            .Select(p => new RemoteAttachment(
                p.FileName ?? "attachment",
                p.ContentType.MimeType,
                p.Content?.Stream is { CanSeek: true } stream ? stream.Length : 0))
            .ToList();

        return new RemoteMessage(
            uid,
            message.MessageId,
            message.Subject ?? string.Empty,
            from?.Name ?? string.Empty,
            string.IsNullOrWhiteSpace(from?.Address) ? UnknownSender : from.Address,
            string.Join(", ", recipients),
            message.Date == DateTimeOffset.MinValue ? fallbackDate : message.Date,
            textBody ?? string.Empty,
            htmlBody,
            isRead,
            attachments);
    }
}
