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

        var htmlBody = MimeTextDecoder.Decode(message.BodyParts.OfType<TextPart>().FirstOrDefault(p => p.IsHtml && !p.IsAttachment));
        var textBody = MimeTextDecoder.Decode(message.BodyParts.OfType<TextPart>().FirstOrDefault(p => p.IsPlain && !p.IsAttachment));
        if (string.IsNullOrWhiteSpace(textBody))
        {
            textBody = HtmlToText.Convert(htmlBody);
        }

        var attachments = MimeAttachments.Enumerate(message)
            .Select(p => new RemoteAttachment(
                p.FileName ?? p.ContentType.Name ?? "attachment",
                p.ContentType.MimeType,
                p.Content?.Stream is { CanSeek: true } stream ? stream.Length : 0))
            .ToList();

        return new RemoteMessage(
            uid,
            message.MessageId,
            DecodeSubject(message),
            DecodeSenderName(message, from),
            string.IsNullOrWhiteSpace(from?.Address) ? UnknownSender : from.Address,
            string.Join(", ", recipients),
            message.Date == DateTimeOffset.MinValue ? fallbackDate : message.Date,
            textBody ?? string.Empty,
            htmlBody,
            isRead,
            attachments);
    }

    /// <summary>
    /// Gets the subject, re-decoding a header that carries raw 8-bit text (no RFC 2047 encoding) with charset guessing.
    /// </summary>
    /// <param name="message">The parsed message.</param>
    /// <returns>The subject text.</returns>
    private static string DecodeSubject(MimeMessage message)
    {
        var raw = FindRaw8BitValue(message, HeaderId.Subject);
        return raw is null ? message.Subject ?? string.Empty : MimeTextDecoder.Decode(raw).Trim();
    }

    /// <summary>
    /// Gets the sender's display name, re-decoding a From header that carries raw 8-bit text with charset guessing.
    /// </summary>
    /// <param name="message">The parsed message.</param>
    /// <param name="from">The first sender mailbox as parsed by MimeKit.</param>
    /// <returns>The display name, possibly empty.</returns>
    private static string DecodeSenderName(MimeMessage message, MailboxAddress? from)
    {
        var raw = FindRaw8BitValue(message, HeaderId.From);
        if (raw is not null
            && InternetAddressList.TryParse(MimeTextDecoder.Decode(raw), out var addresses)
            && addresses.Mailboxes.FirstOrDefault() is { } mailbox)
        {
            return mailbox.Name ?? string.Empty;
        }

        return from?.Name ?? string.Empty;
    }

    /// <summary>
    /// Returns the raw bytes of a header when they contain 8-bit data, which MimeKit can only decode by assuming a charset.
    /// </summary>
    /// <param name="message">The parsed message.</param>
    /// <param name="id">The header.</param>
    /// <returns>The raw value, or <see langword="null"/> when the header is missing or 7-bit.</returns>
    private static byte[]? FindRaw8BitValue(MimeMessage message, HeaderId id)
    {
        var raw = message.Headers.FirstOrDefault(h => h.Id == id)?.RawValue;
        return raw is not null && raw.Any(b => b >= 0x80) ? raw : null;
    }
}
