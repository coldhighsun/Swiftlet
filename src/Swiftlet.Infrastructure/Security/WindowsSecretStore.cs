using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Swiftlet.Application.Abstractions;

namespace Swiftlet.Infrastructure.Security;

/// <summary>
/// Stores secrets in files encrypted with the Windows Data Protection API, bound to the current Windows user.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsSecretStore : ISecretStore
{
    /// <summary>
    /// Application-specific entropy so other programs of the same user cannot decrypt the files by default.
    /// </summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Swiftlet.Secrets.v1");

    /// <summary>
    /// The directory holding the encrypted secret files.
    /// </summary>
    private readonly string _directory;

    /// <summary>
    /// Initializes a new instance of the <see cref="WindowsSecretStore"/> class.
    /// </summary>
    /// <param name="directory">The directory for the encrypted files; created if missing.</param>
    public WindowsSecretStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    /// <inheritdoc />
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = PathFor(key);
        if (!File.Exists(path))
        {
            return null;
        }

        var encrypted = await File.ReadAllBytesAsync(path, cancellationToken);
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser));
    }

    /// <inheritdoc />
    public async Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);
        var path = PathFor(key);
        var temporary = $"{path}.tmp";

        // The database key must survive a power loss once data is encrypted with it, so the file is flushed to disk
        // before it atomically replaces the old one.
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await stream.WriteAsync(encrypted, cancellationToken);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        File.Delete(PathFor(key));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Maps a secret key to a file path by hashing it.
    /// </summary>
    /// <param name="key">The secret key.</param>
    /// <returns>The file path.</returns>
    private string PathFor(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return Path.Combine(_directory, $"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))}.dpapi");
    }
}
