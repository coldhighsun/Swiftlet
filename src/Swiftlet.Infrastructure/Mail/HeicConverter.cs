using ImageMagick;
using Microsoft.Extensions.Logging;

namespace Swiftlet.Infrastructure.Mail;

/// <summary>
/// Converts HEIC / HEIF images, which the embedded web view cannot display, to JPEG.
/// </summary>
internal static class HeicConverter
{
    /// <summary>
    /// The longest side of a converted image; larger images are scaled down to keep the inlined data small.
    /// </summary>
    private const uint MaxSide = 2048;

    /// <summary>
    /// The JPEG quality of converted images.
    /// </summary>
    private const uint JpegQuality = 85;

    /// <summary>
    /// Gets a value indicating whether a MIME type names a HEIC / HEIF image.
    /// </summary>
    /// <param name="mimeType">The MIME type.</param>
    /// <returns><see langword="true"/> for image/heic, image/heif and their sequence variants.</returns>
    public static bool IsHeic(string mimeType)
    {
        return mimeType.Equals("image/heic", StringComparison.OrdinalIgnoreCase)
            || mimeType.Equals("image/heif", StringComparison.OrdinalIgnoreCase)
            || mimeType.Equals("image/heic-sequence", StringComparison.OrdinalIgnoreCase)
            || mimeType.Equals("image/heif-sequence", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets a value indicating whether a generic binary part is a HEIC photo judging by its file name.
    /// </summary>
    /// <param name="mimeType">The MIME type of the part.</param>
    /// <param name="fileName">The file name of the part, if any.</param>
    /// <returns><see langword="true"/> for application/octet-stream parts named *.heic or *.heif.</returns>
    public static bool IsHeicFile(string mimeType, string? fileName)
    {
        return mimeType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)
            && fileName is not null
            && (fileName.EndsWith(".heic", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".heif", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Converts a HEIC image to JPEG.
    /// </summary>
    /// <param name="data">The HEIC bytes.</param>
    /// <param name="logger">The logger that records why a conversion failed; optional.</param>
    /// <returns>The JPEG bytes, or <see langword="null"/> when the data cannot be decoded.</returns>
    public static byte[]? ToJpeg(byte[] data, ILogger? logger = null)
    {
        try
        {
            using var image = new MagickImage(data);
            if (image.Width > MaxSide || image.Height > MaxSide)
            {
                image.Resize(MaxSide, MaxSide);
            }

            // Respect the camera orientation, then drop it so viewers do not rotate the image twice.
            image.AutoOrient();
            image.Format = MagickFormat.Jpeg;
            image.Quality = JpegQuality;
            return image.ToByteArray();
        }
        catch (MagickException ex)
        {
            logger?.LogWarning(ex, "HEIC decoding failed");
            // Corrupt or unsupported HEIC data: the image is simply left out, the rest of the mail still shows.
            return null;
        }
    }
}
