using System.Security.Cryptography;
using Swiftlet.Application.Abstractions;

namespace Swiftlet.Infrastructure.Persistence.Encryption;

/// <summary>
/// Reads and creates the key that encrypts all databases of the application.
/// </summary>
/// <param name="secrets">The secret store holding the key.</param>
public sealed class DatabaseKeyProvider(ISecretStore secrets)
{
    /// <summary>
    /// The secret store entry of the database key.
    /// </summary>
    public const string SecretKey = "database:key";

    /// <summary>
    /// The key length in bytes.
    /// </summary>
    private const int KeyBytes = 32;

    /// <summary>
    /// Reads the stored key.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The key as 64 hexadecimal characters, or <see langword="null"/> when none is stored.</returns>
    /// <exception cref="InvalidDataException">The stored value is not a key.</exception>
    public async Task<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        var value = await secrets.GetAsync(SecretKey, cancellationToken);
        if (value is null)
        {
            return null;
        }

        return value.Length == KeyBytes * 2 && value.All(char.IsAsciiHexDigit)
            ? value
            : throw new InvalidDataException("The stored database key is damaged.");
    }

    /// <summary>
    /// Generates a key and stores it, replacing any earlier one.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The new key.</returns>
    /// <exception cref="InvalidOperationException">The store did not keep the key.</exception>
    public async Task<string> CreateAsync(CancellationToken cancellationToken = default)
    {
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(KeyBytes));
        await secrets.SetAsync(SecretKey, key, cancellationToken);

        // A database must never be encrypted with a key that cannot be read again.
        var stored = await secrets.GetAsync(SecretKey, cancellationToken);
        return string.Equals(stored, key, StringComparison.Ordinal)
            ? key
            : throw new InvalidOperationException("The secret store did not keep the database key.");
    }
}
