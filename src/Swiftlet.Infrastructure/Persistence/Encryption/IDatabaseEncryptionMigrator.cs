namespace Swiftlet.Infrastructure.Persistence.Encryption;

/// <summary>
/// Converts a plain database into an encrypted one.
/// </summary>
public interface IDatabaseEncryptionMigrator
{
    /// <summary>
    /// Encrypts a plain database in place. Nobody else may use the database meanwhile.
    /// </summary>
    /// <param name="path">The database file.</param>
    /// <param name="keyHex">The key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the database is encrypted.</returns>
    /// <exception cref="DatabaseConversionException">The database could not be converted.</exception>
    Task EncryptAsync(string path, string keyHex, CancellationToken cancellationToken = default);
}
