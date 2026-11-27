using System.Text;

namespace Swiftlet.Application.Contacts;

/// <summary>
/// Decodes the text of a contact file, whose encoding is not declared.
/// </summary>
internal static class ContactFileText
{
    /// <summary>
    /// The code page of GB18030, used by Chinese editions of spreadsheet and mail programs.
    /// </summary>
    private const int Gb18030CodePage = 54936;

    /// <summary>
    /// Decodes the bytes: a byte order mark decides, otherwise valid UTF-8 is taken as such and anything else is read
    /// as GB18030.
    /// </summary>
    /// <param name="content">The file content.</param>
    /// <returns>The text without byte order mark.</returns>
    public static string Decode(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);

        ReadOnlySpan<byte> bytes = content;
        if (bytes.StartsWith(Encoding.UTF8.Preamble))
        {
            return Encoding.UTF8.GetString(bytes[Encoding.UTF8.Preamble.Length..]);
        }

        if (bytes.StartsWith(Encoding.Unicode.Preamble))
        {
            return Encoding.Unicode.GetString(bytes[2..]);
        }

        if (bytes.StartsWith(Encoding.BigEndianUnicode.Preamble))
        {
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        }

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Not UTF-8: the remaining common case is a file written with the Chinese system code page.
            var gb18030 = CodePagesEncodingProvider.Instance.GetEncoding(Gb18030CodePage) ?? Encoding.Latin1;
            return gb18030.GetString(bytes);
        }
    }
}
