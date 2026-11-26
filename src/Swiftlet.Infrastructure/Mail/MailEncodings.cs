using System.Text;

namespace Swiftlet.Infrastructure.Mail;

/// <summary>
/// Makes the legacy code pages used by real-world mail (GBK, GB2312, Big5, Windows-125x, ...) available to .NET.
/// </summary>
public static class MailEncodings
{
    /// <summary>
    /// Registers the code pages encoding provider; calling it more than once is harmless.
    /// </summary>
    /// <remarks>
    /// Call it before anything in the process uses MimeKit: MimeKit caches the charsets it cannot resolve, so mail
    /// decoded after a late registration still comes out as Latin-1 mojibake.
    /// </remarks>
    public static void Register()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }
}
