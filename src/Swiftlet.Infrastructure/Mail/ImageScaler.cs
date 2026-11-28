using SkiaSharp;

namespace Swiftlet.Infrastructure.Mail;

/// <summary>
/// An image produced by <see cref="ImageScaler"/>.
/// </summary>
/// <param name="Content">The encoded bytes.</param>
/// <param name="ContentType">The MIME type, image/png or image/jpeg.</param>
internal sealed record ScaledImage(byte[] Content, string ContentType);

/// <summary>
/// Scales images down with SkiaSharp. The input is untrusted, so its size is checked before it is decoded.
/// </summary>
internal static class ImageScaler
{
    /// <summary>
    /// The most pixels an image may have before it is refused (guards against decompression bombs).
    /// </summary>
    private const long MaxPixels = 100_000_000;

    /// <summary>
    /// Decodes an image, turns it upright and scales it down; small images keep their size.
    /// </summary>
    /// <param name="data">The encoded image.</param>
    /// <param name="maxSide">The longest side of the result, in pixels.</param>
    /// <param name="quality">The JPEG quality.</param>
    /// <param name="keepAlpha">Whether an image with transparency becomes a PNG; otherwise it is put on white.</param>
    /// <returns>The result, or <see langword="null"/> when the data cannot be decoded or is too large.</returns>
    public static ScaledImage? Scale(ReadOnlySpan<byte> data, int maxSide, int quality, bool keepAlpha)
    {
        using var buffer = SKData.CreateCopy(data);
        using var codec = SKCodec.Create(buffer);
        if (codec is null)
        {
            return null;
        }

        var size = codec.Info.Size;
        if (size.Width <= 0 || size.Height <= 0 || (long)size.Width * size.Height > MaxPixels)
        {
            return null;
        }

        var transparent = keepAlpha && codec.Info.AlphaType != SKAlphaType.Opaque;
        using var source = new SKBitmap();
        if (!source.TryAllocPixels(new SKImageInfo(size.Width, size.Height, SKImageInfo.PlatformColorType, SKAlphaType.Premul)))
        {
            return null;
        }

        // A truncated file still shows the part that arrived.
        var decoded = codec.GetPixels(source.Info, source.GetPixels());
        if (decoded is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            return null;
        }

        var factor = Math.Min(1d, (double)maxSide / Math.Max(size.Width, size.Height));
        var width = Math.Max(1, (int)Math.Round(size.Width * factor));
        var height = Math.Max(1, (int)Math.Round(size.Height * factor));
        using var resized = factor < 1d
            ? source.Resize(source.Info.WithSize(width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear))
            : null;
        if (factor < 1d && resized is null)
        {
            return null;
        }

        // Origins from LeftTop on are rotated by a quarter turn, which swaps the sides.
        var origin = codec.EncodedOrigin;
        var turned = origin >= SKEncodedOrigin.LeftTop;
        using var target = new SKBitmap(source.Info.WithSize(turned ? height : width, turned ? width : height));
        using (var canvas = new SKCanvas(target))
        {
            canvas.Clear(transparent ? SKColors.Transparent : SKColors.White);
            canvas.SetMatrix(Upright(origin, target.Width, target.Height));
            canvas.DrawBitmap(resized ?? source, 0, 0);
        }

        var format = transparent ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg;
        using var encoded = target.Encode(format, quality);
        return encoded is null
            ? null
            : new ScaledImage(encoded.ToArray(), transparent ? "image/png" : "image/jpeg");
    }

    /// <summary>
    /// Gets the transformation that turns the stored pixels the way the camera was held.
    /// </summary>
    /// <param name="origin">The orientation stored in the image.</param>
    /// <param name="width">The width of the upright image.</param>
    /// <param name="height">The height of the upright image.</param>
    /// <returns>The matrix.</returns>
    private static SKMatrix Upright(SKEncodedOrigin origin, int width, int height)
    {
        return origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, width, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, width, 0, -1, height, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, height, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, width, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, width, -1, 0, height, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, height, 0, 0, 1),
            _ => SKMatrix.Identity,
        };
    }
}
