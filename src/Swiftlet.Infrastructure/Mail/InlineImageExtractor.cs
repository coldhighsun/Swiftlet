using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Queries;

namespace Swiftlet.Infrastructure.Mail;

/// <summary>
/// Extracts the images embedded in a MIME message, the ones HTML refers to with <c>cid:</c> URLs.
/// </summary>
internal static class InlineImageExtractor
{
    /// <summary>
    /// The largest single image taken from a message.
    /// </summary>
    private const int MaxImageBytes = 2 * 1024 * 1024;

    /// <summary>
    /// The largest single source image taken from a message; phone photos are several MB but shrink a lot once converted to a scaled-down JPEG.
    /// </summary>
    private const int MaxSourceBytes = 25 * 1024 * 1024;

    /// <summary>
    /// The largest total size of the images taken from one message.
    /// </summary>
    private const int MaxTotalBytes = 10 * 1024 * 1024;

    /// <summary>
    /// The raster image types that are safe to inline; SVG is excluded because it can carry script.
    /// </summary>
    private static readonly HashSet<string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/jpg",
        "image/gif",
        "image/webp",
        "image/bmp",
    };

    /// <summary>
    /// Extracts the wanted embedded images.
    /// </summary>
    /// <param name="message">The downloaded message.</param>
    /// <param name="wanted">The normalized content identifiers to extract.</param>
    /// <param name="logger">The logger that records what happened to each image; optional.</param>
    /// <returns>The images keyed by normalized content identifier.</returns>
    public static IReadOnlyDictionary<string, InlineImage> Extract(
        MimeMessage message,
        IReadOnlyCollection<string> wanted,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(wanted);

        logger ??= NullLogger.Instance;
        var result = new Dictionary<string, InlineImage>(StringComparer.OrdinalIgnoreCase);
        var total = 0L;
        foreach (var part in message.BodyParts.OfType<MimePart>())
        {
            var mimeType = part.ContentType.MimeType;
            var fileName = part.FileName ?? part.ContentType.Name;
            var isHeic = HeicConverter.IsHeic(mimeType) || HeicConverter.IsHeicFile(mimeType, fileName);
            if (string.IsNullOrWhiteSpace(part.ContentId) || part.Content is null)
            {
                continue;
            }

            var id = InlineImageReferences.Normalize(part.ContentId);
            var isWanted = wanted.Contains(id, StringComparer.OrdinalIgnoreCase);
            logger.LogInformation(
                "Inline part cid={ContentId} type={MimeType} file={FileName} wanted={Wanted}",
                id,
                mimeType,
                fileName,
                isWanted);
            if (!isWanted || result.ContainsKey(id))
            {
                continue;
            }

            if (!isHeic && !AllowedTypes.Contains(mimeType))
            {
                logger.LogWarning("Inline image {ContentId} skipped: unsupported type {MimeType}", id, mimeType);
                continue;
            }

            using var buffer = new MemoryStream();
            part.Content.DecodeTo(buffer);
            if (buffer.Length > MaxSourceBytes)
            {
                logger.LogWarning("Inline image {ContentId} skipped: {Size} bytes is over the limit", id, buffer.Length);
                continue;
            }

            var bytes = buffer.ToArray();
            var type = mimeType.ToLowerInvariant();

            // HEIC cannot be shown by the web view, and images over the size limit (phone photos) are scaled down.
            // Large GIFs are skipped rather than converted, which would lose the animation.
            var oversized = bytes.Length > MaxImageBytes;
            if (oversized && type == "image/gif")
            {
                logger.LogWarning("Inline image {ContentId} skipped: GIF of {Size} bytes is over the limit", id, bytes.Length);
                continue;
            }

            if (isHeic || oversized)
            {
                if (HeicConverter.ToJpeg(bytes, logger) is not { } jpeg)
                {
                    logger.LogWarning("Inline image {ContentId} skipped: conversion failed", id);
                    continue;
                }

                logger.LogInformation("Inline image {ContentId} converted to JPEG: {From} to {To} bytes", id, bytes.Length, jpeg.Length);
                bytes = jpeg;
                type = "image/jpeg";
            }

            if (total + bytes.Length > MaxTotalBytes)
            {
                logger.LogWarning("Inline image {ContentId} skipped: message total is over the limit", id);
                continue;
            }

            total += bytes.Length;
            result[id] = new InlineImage(type, bytes);
        }

        var missing = wanted.Where(w => !result.ContainsKey(w)).ToList();
        if (missing.Count > 0)
        {
            logger.LogWarning("Inline images not available for {Missing}", string.Join(", ", missing));
        }

        return result;
    }
}
