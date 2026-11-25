using MimeKit;
using MimeKit.Utils;
using Swiftlet.Application.Abstractions;
using Swiftlet.Domain.Accounts;

namespace Swiftlet.Infrastructure.Mail;

/// <summary>
/// Builds MIME messages from user-composed <see cref="OutgoingMessage"/> values.
/// </summary>
internal static class OutgoingMimeBuilder
{
    /// <summary>
    /// Builds the MIME message to send.
    /// </summary>
    /// <param name="account">The sending account.</param>
    /// <param name="outgoing">The composed message.</param>
    /// <returns>The MIME message.</returns>
    public static MimeMessage Build(MailAccount account, OutgoingMessage outgoing)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(outgoing);

        if (outgoing.To.Count + outgoing.Cc.Count + outgoing.Bcc.Count == 0)
        {
            throw new ArgumentException("At least one recipient is required.", nameof(outgoing));
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(account.DisplayName, account.EmailAddress));
        message.To.AddRange(outgoing.To.Select(MailboxAddress.Parse));
        message.Cc.AddRange(outgoing.Cc.Select(MailboxAddress.Parse));
        message.Bcc.AddRange(outgoing.Bcc.Select(MailboxAddress.Parse));
        message.Subject = outgoing.Subject;
        message.MessageId = MimeUtils.GenerateMessageId();
        if (!string.IsNullOrWhiteSpace(outgoing.InReplyTo))
        {
            message.InReplyTo = outgoing.InReplyTo;
            message.References.Add(outgoing.InReplyTo);
        }

        var body = new BodyBuilder { TextBody = outgoing.TextBody, HtmlBody = outgoing.HtmlBody };
        foreach (var attachment in outgoing.Attachments)
        {
            var contentType = string.IsNullOrWhiteSpace(attachment.ContentType)
                || attachment.ContentType == "application/octet-stream"
                ? MimeTypes.GetMimeType(attachment.FileName)
                : attachment.ContentType;
            body.Attachments.Add(attachment.FileName, attachment.Content.ToArray(), ContentType.Parse(contentType));
        }

        message.Body = body.ToMessageBody();
        return message;
    }
}
