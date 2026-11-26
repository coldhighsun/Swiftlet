using System.IO.Compression;
using System.Text;

namespace Swiftlet.Infrastructure.Persistence;

/// <summary>
/// The stored form of an HTML body: either a two-byte marker followed by the raw Deflate stream of its UTF-8 bytes, or,
/// when compressing does not make it smaller, the UTF-8 bytes themselves. Rows written before compression existed hold
/// plain text, which reads as the second form.
/// </summary>
internal static class HtmlBodyCodec
{
    /// <summary>
    /// The marker of a compressed body. The byte 0xFF never occurs in UTF-8, so plain text cannot be mistaken for it.
    /// </summary>
    private static readonly byte[] Marker = [0xFF, 0x01];

    /// <summary>
    /// Converts a body into its stored form.
    /// </summary>
    /// <param name="html">The HTML body.</param>
    /// <returns>The bytes to store.</returns>
    public static byte[] Encode(string html)
    {
        var plain = Encoding.UTF8.GetBytes(html);
        using var buffer = new MemoryStream();
        buffer.Write(Marker);
        using (var deflate = new DeflateStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(plain);
        }

        return buffer.Length < plain.Length ? buffer.ToArray() : plain;
    }

    /// <summary>
    /// Converts a stored value back into the body.
    /// </summary>
    /// <param name="stored">The stored bytes.</param>
    /// <returns>The HTML body.</returns>
    public static string Decode(byte[] stored)
    {
        if (!stored.AsSpan().StartsWith(Marker))
        {
            return Encoding.UTF8.GetString(stored);
        }

        using var source = new MemoryStream(stored, Marker.Length, stored.Length - Marker.Length, writable: false);
        using var deflate = new DeflateStream(source, CompressionMode.Decompress);
        using var plain = new MemoryStream();
        deflate.CopyTo(plain);
        return Encoding.UTF8.GetString(plain.GetBuffer(), 0, (int)plain.Length);
    }
}
