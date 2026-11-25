using System.Security.Cryptography;
using System.Text;

namespace Swiftlet.Infrastructure.OAuth;

/// <summary>
/// Proof Key for Code Exchange (RFC 7636) helpers.
/// </summary>
internal static class Pkce
{
    /// <summary>
    /// Creates a random, URL-safe string, suitable as a code verifier or state value.
    /// </summary>
    /// <param name="byteCount">The number of random bytes.</param>
    /// <returns>The Base64Url-encoded random string.</returns>
    public static string CreateRandomString(int byteCount = 32)
    {
        return Base64Url(RandomNumberGenerator.GetBytes(byteCount));
    }

    /// <summary>
    /// Computes the S256 code challenge of a verifier.
    /// </summary>
    /// <param name="verifier">The code verifier.</param>
    /// <returns>The code challenge.</returns>
    public static string CreateChallenge(string verifier)
    {
        return Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
    }

    /// <summary>
    /// Encodes bytes as unpadded Base64Url.
    /// </summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The encoded text.</returns>
    private static string Base64Url(byte[] bytes)
    {
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
