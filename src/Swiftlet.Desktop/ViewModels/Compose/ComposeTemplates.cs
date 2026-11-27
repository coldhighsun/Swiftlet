using System.Globalization;
using System.Text;
using Swiftlet.Desktop.Localization;
using Swiftlet.Desktop.Services;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Desktop.ViewModels.Compose;

/// <summary>
/// Builds the initial content of reply and forward messages.
/// </summary>
public static class ComposeTemplates
{
    /// <summary>
    /// Builds the compose request replying to a message.
    /// </summary>
    /// <param name="message">The message being replied to.</param>
    /// <param name="localizer">The localizer for the quote header.</param>
    /// <returns>The compose request.</returns>
    public static ComposeRequest Reply(MailMessage message, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(localizer);
        var body = new StringBuilder("\n\n");
        body.Append(localizer.Format("Template_ReplyHeader", FormatTime(message), Sender(message))).Append('\n');
        body.Append(Quote(message.BodyText));
        return new ComposeRequest(
            ComposeMode.Reply,
            message.AccountId,
            message.FromAddress,
            Prefix(message.Subject, "Re:", "回复:", "回复："),
            body.ToString(),
            message.MessageIdHeader,
            body.ToString().TrimStart('\n'),
            message.Id);
    }

    /// <summary>
    /// Builds the compose request forwarding a message. Attachments are not forwarded.
    /// </summary>
    /// <param name="message">The message to forward.</param>
    /// <param name="localizer">The localizer for the forward header.</param>
    /// <returns>The compose request.</returns>
    public static ComposeRequest Forward(MailMessage message, ILocalizer localizer)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(localizer);
        var body = new StringBuilder("\n\n");
        body.Append(localizer["Template_ForwardMarker"]).Append('\n');
        body.Append(localizer.Format("Template_From", Sender(message))).Append('\n');
        body.Append(localizer.Format("Template_Date", FormatTime(message))).Append('\n');
        body.Append(localizer.Format("Template_Subject", message.Subject)).Append('\n');
        body.Append(localizer.Format("Template_To", message.Recipients)).Append("\n\n");
        body.Append(message.BodyText);
        return new ComposeRequest(
            ComposeMode.Forward,
            message.AccountId,
            string.Empty,
            Prefix(message.Subject, "Fwd:", "Fw:", "转发:", "转发："),
            body.ToString(),
            null,
            body.ToString().TrimStart('\n'));
    }

    /// <summary>
    /// Adds a reply or forward prefix to a subject unless it already starts with one.
    /// Prefixes of both supported languages count as already present.
    /// </summary>
    /// <param name="subject">The original subject.</param>
    /// <param name="prefix">The prefix to add.</param>
    /// <param name="existingPrefixes">Other prefixes that count as already present.</param>
    /// <returns>The subject to use.</returns>
    private static string Prefix(string subject, string prefix, params string[] existingPrefixes)
    {
        var trimmed = (subject ?? string.Empty).Trim();
        var present = existingPrefixes.Append(prefix)
            .Any(p => trimmed.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        return present ? trimmed : $"{prefix} {trimmed}".TrimEnd();
    }

    /// <summary>
    /// Prefixes every line of a text with the quote marker.
    /// </summary>
    /// <param name="text">The text to quote.</param>
    /// <returns>The quoted text.</returns>
    private static string Quote(string text)
    {
        var lines = (text ?? string.Empty).ReplaceLineEndings("\n").Split('\n');
        return string.Join('\n', lines.Select(l => $"> {l}".TrimEnd()));
    }

    /// <summary>
    /// Formats the sent time of a message in the local time zone.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <returns>The formatted time.</returns>
    private static string FormatTime(MailMessage message)
    {
        return message.SentAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Formats the sender of a message as <c>Name &lt;address&gt;</c>.
    /// </summary>
    /// <param name="message">The message.</param>
    /// <returns>The formatted sender.</returns>
    private static string Sender(MailMessage message)
    {
        return string.IsNullOrWhiteSpace(message.FromName) ? message.FromAddress : $"{message.FromName} <{message.FromAddress}>";
    }
}
