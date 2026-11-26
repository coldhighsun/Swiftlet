using AwesomeAssertions;
using MimeKit;
using Swiftlet.Infrastructure.Mail;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Tests for <see cref="MimeAttachments"/> and how the mapper lists inline images with a file name.
/// </summary>
public class MimeAttachmentsTests
{
    /// <summary>
    /// Builds a message with a regular attachment and the given inline resources.
    /// </summary>
    /// <param name="inline">The inline resources as (file name, Content-ID); a null file name means no name.</param>
    /// <returns>The message.</returns>
    private static MimeMessage Build(params (string? FileName, string ContentId)[] inline)
    {
        var builder = new BodyBuilder { HtmlBody = "<p>hi</p>", TextBody = "hi" };
        builder.Attachments.Add("data.bin", [1, 2, 3, 4], ContentType.Parse("application/octet-stream"));
        foreach (var (fileName, contentId) in inline)
        {
            var part = (MimePart)builder.LinkedResources.Add(fileName ?? "x", [9, 9], ContentType.Parse("image/jpeg"));
            part.ContentId = contentId;
            if (fileName is null)
            {
                part.FileName = null;
                part.ContentType.Name = null;
            }
        }

        return new MimeMessage { Body = builder.ToMessageBody() };
    }

    /// <summary>
    /// Verifies regular attachments come first in their original order and named inline parts follow.
    /// </summary>
    [Fact]
    public void Enumerate_NamedInlineImage_IsAppendedAfterRegularAttachments()
    {
        var message = Build(("IMG_6686.HEIC", "a@x"));

        var parts = MimeAttachments.Enumerate(message);

        parts.Select(p => p.FileName).Should().Equal("data.bin", "IMG_6686.HEIC");
        parts[0].Should().BeSameAs(message.Attachments.OfType<MimePart>().Single());
    }

    /// <summary>
    /// Verifies inline images without a file name are not listed.
    /// </summary>
    [Fact]
    public void Enumerate_UnnamedInlineImage_IsNotListed()
    {
        var message = Build((null, "a@x"));

        MimeAttachments.Enumerate(message).Select(p => p.FileName).Should().Equal("data.bin");
    }

    /// <summary>
    /// Verifies a message with only regular attachments is listed exactly as MimeKit reports it, and body parts are never listed.
    /// </summary>
    [Fact]
    public void Enumerate_OnlyRegularAttachments_MatchesMimeKit()
    {
        var message = Build();

        var parts = MimeAttachments.Enumerate(message);

        parts.Should().Equal(message.Attachments.OfType<MimePart>());
        parts.Should().NotContain(p => p is TextPart);
    }

    /// <summary>
    /// Verifies the mapper lists a named inline image as an attachment after the regular ones.
    /// </summary>
    [Fact]
    public void Map_NamedInlineImage_ListedAsAttachment()
    {
        var message = Build(("IMG_6686.HEIC", "a@x"));

        var mapped = MimeMessageMapper.Map(1, message, isRead: false, DateTimeOffset.MinValue);

        mapped.Attachments.Select(a => a.FileName).Should().Equal("data.bin", "IMG_6686.HEIC");
        mapped.Attachments[1].ContentType.Should().Be("image/jpeg");
        mapped.Attachments[1].Size.Should().Be(2);
    }
}
