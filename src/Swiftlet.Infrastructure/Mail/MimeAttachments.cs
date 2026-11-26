using MimeKit;

namespace Swiftlet.Infrastructure.Mail;

/// <summary>
/// Lists the parts of a message that are offered as attachments, in a fixed order shared by synchronization and download.
/// </summary>
internal static class MimeAttachments
{
    /// <summary>
    /// Enumerates the attachments of a message: first the parts MimeKit reports as attachments (in the same order as before,
    /// so stored ordinals stay valid), then inline parts that have a file name, such as photos shown in the body.
    /// </summary>
    /// <param name="message">The parsed message.</param>
    /// <returns>The attachment parts; the position in the list is the attachment ordinal.</returns>
    public static IReadOnlyList<MimePart> Enumerate(MimeMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var result = message.Attachments.OfType<MimePart>().ToList();
        var seen = new HashSet<MimePart>(result);
        foreach (var part in message.BodyParts.OfType<MimePart>())
        {
            if (part is TextPart || seen.Contains(part) || part.Content is null)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(part.FileName ?? part.ContentType.Name))
            {
                continue;
            }

            seen.Add(part);
            result.Add(part);
        }

        return result;
    }
}
