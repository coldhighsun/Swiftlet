using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Swiftlet.Infrastructure.Tests;

/// <summary>
/// Builds images for tests.
/// </summary>
internal static class TestImages
{
    /// <summary>
    /// Builds an image of one color.
    /// </summary>
    /// <param name="width">The width.</param>
    /// <param name="height">The height.</param>
    /// <param name="format">The format.</param>
    /// <param name="color">The color; steel blue when omitted.</param>
    /// <returns>The encoded image.</returns>
    public static byte[] Solid(int width, int height, SKEncodedImageFormat format, SKColor? color = null)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(color ?? SKColors.SteelBlue);
        using var data = bitmap.Encode(format, 90);
        return data.ToArray();
    }

    /// <summary>
    /// Builds a PNG of random pixels, which hardly compresses.
    /// </summary>
    /// <param name="side">The width and height.</param>
    /// <returns>The encoded image.</returns>
    public static byte[] Noise(int side)
    {
        using var bitmap = new SKBitmap(side, side, SKColorType.Rgba8888, SKAlphaType.Opaque);
        var pixels = new byte[bitmap.ByteCount];
        new Random(1).NextBytes(pixels);
        for (var i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 0xFF;
        }

        Marshal.Copy(pixels, 0, bitmap.GetPixels(), pixels.Length);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>
    /// Inserts an EXIF segment holding only the orientation into a JPEG.
    /// </summary>
    /// <param name="jpeg">The JPEG.</param>
    /// <param name="orientation">The EXIF orientation, 1 to 8.</param>
    /// <returns>The JPEG with the segment.</returns>
    public static byte[] WithOrientation(byte[] jpeg, byte orientation)
    {
        byte[] segment =
        [
            0xFF, 0xE1, 0x00, 0x22, (byte)'E', (byte)'x', (byte)'i', (byte)'f', 0x00, 0x00,
            (byte)'M', (byte)'M', 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08,
            0x00, 0x01, 0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, orientation, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        ];
        return [.. jpeg[..2], .. segment, .. jpeg[2..]];
    }

    /// <summary>
    /// Builds a small PNG whose header declares another size.
    /// </summary>
    /// <param name="width">The declared width.</param>
    /// <param name="height">The declared height.</param>
    /// <returns>The bytes.</returns>
    public static byte[] PngHeader(int width, int height)
    {
        var png = Solid(1, 1, SKEncodedImageFormat.Png);

        // The IHDR chunk starts at byte 12: type, width, height, five single bytes, then the CRC of all of them.
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), height);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(29), Crc32(png.AsSpan(12, 17)));
        return png;
    }

    /// <summary>
    /// Computes the CRC-32 that PNG chunks carry.
    /// </summary>
    /// <param name="data">The bytes.</param>
    /// <returns>The checksum.</returns>
    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }
}
