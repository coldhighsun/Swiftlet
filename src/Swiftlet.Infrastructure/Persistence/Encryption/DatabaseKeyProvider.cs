using System.Security.Cryptography;
using System.Text;
using Swiftlet.Application.Abstractions;

namespace Swiftlet.Infrastructure.Persistence.Encryption;

/// <summary>
/// Reads and creates the key that encrypts all databases of the application. Every key gets a secret store entry of its
/// own, so creating one can never overwrite an earlier key, even when the store wrongly reports that it has none.
/// </summary>
/// <param name="secrets">The secret store holding the keys.</param>
/// <param name="nameFile">The file listing the entry names, one per line, the current one last.</param>
public sealed class DatabaseKeyProvider(ISecretStore secrets, string nameFile)
{
    /// <summary>
    /// The beginning of every secret store entry name of a database key.
    /// </summary>
    private const string SecretPrefix = "database:key:";

    /// <summary>
    /// The key length in bytes.
    /// </summary>
    private const int KeyBytes = 32;

    /// <summary>
    /// Reads the current key.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The key as 64 hexadecimal characters, or <see langword="null"/> when none is stored.</returns>
    /// <exception cref="InvalidDataException">The stored value is not a key, or the name file is damaged.</exception>
    public async Task<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(nameFile))
        {
            return null;
        }

        var names = await ReadNamesAsync(cancellationToken);
        if (names.Count == 0 || !names[^1].StartsWith(SecretPrefix, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The file naming the database key is damaged.");
        }

        var value = await secrets.GetAsync(names[^1], cancellationToken);
        if (value is null)
        {
            return null;
        }

        return value.Length == KeyBytes * 2 && value.All(char.IsAsciiHexDigit)
            ? value
            : throw new InvalidDataException("The stored database key is damaged.");
    }

    /// <summary>
    /// Generates a key, stores it under a new name and makes it the current one.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The new key.</returns>
    /// <exception cref="InvalidOperationException">The store did not keep the key.</exception>
    public async Task<string> CreateAsync(CancellationToken cancellationToken = default)
    {
        var key = Convert.ToHexString(RandomNumberGenerator.GetBytes(KeyBytes));
        var name = $"{SecretPrefix}{Guid.NewGuid():N}";
        await secrets.SetAsync(name, key, cancellationToken);

        // A database must never be encrypted with a key that cannot be read again.
        var stored = await secrets.GetAsync(name, cancellationToken);
        if (!string.Equals(stored, key, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The secret store did not keep the database key.");
        }

        await AppendNameAsync(name, cancellationToken);
        return key;
    }

    /// <summary>
    /// Reads the recorded entry names.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The names, oldest first.</returns>
    private async Task<List<string>> ReadNamesAsync(CancellationToken cancellationToken)
    {
        var lines = await File.ReadAllLinesAsync(nameFile, cancellationToken);
        return [.. lines.Select(line => line.Trim()).Where(line => line.Length > 0)];
    }

    /// <summary>
    /// Records a new current entry name, keeping the earlier ones for data that was put aside.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the name is on disk.</returns>
    private async Task AppendNameAsync(string name, CancellationToken cancellationToken)
    {
        var names = File.Exists(nameFile) ? await ReadNamesAsync(cancellationToken) : [];
        names.Add(name);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(nameFile))!);
        var temporary = $"{nameFile}.tmp";

        // Data is encrypted as soon as this returns, so the name must survive a power loss like the key itself.
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await stream.WriteAsync(Encoding.UTF8.GetBytes(string.Join('\n', names) + '\n'), cancellationToken);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, nameFile, overwrite: true);
    }
}
