using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Swiftlet.Application.Abstractions;

namespace Swiftlet.Infrastructure.Security;

/// <summary>
/// Stores secrets as ASP.NET Core Data Protection payloads in files under a directory.
/// A platform keychain implementation replaces this in the OAuth milestone.
/// </summary>
public sealed class DataProtectionSecretStore : ISecretStore, IDisposable
{
    /// <summary>
    /// The private service provider hosting the Data Protection stack.
    /// </summary>
    private readonly ServiceProvider _serviceProvider;

    /// <summary>
    /// The protector encrypting and decrypting secret values.
    /// </summary>
    private readonly IDataProtector _protector;

    /// <summary>
    /// The directory holding the encrypted secret files.
    /// </summary>
    private readonly string _secretsDirectory;

    /// <summary>
    /// Initializes a new instance of the <see cref="DataProtectionSecretStore"/> class.
    /// </summary>
    /// <param name="directory">The root directory for keys and secrets; created if missing.</param>
    public DataProtectionSecretStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        _secretsDirectory = Path.Combine(directory, "secrets");
        Directory.CreateDirectory(_secretsDirectory);

        var services = new ServiceCollection();
        services.AddDataProtection()
            .SetApplicationName("Swiftlet")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(directory, "keys")));
        _serviceProvider = services.BuildServiceProvider();
        _protector = _serviceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("Swiftlet.Secrets.v1");
    }

    /// <summary>
    /// Releases the internal service provider.
    /// </summary>
    public void Dispose()
    {
        _serviceProvider.Dispose();
    }

    /// <inheritdoc />
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var path = PathFor(key);
        if (!File.Exists(path))
        {
            return null;
        }

        var protectedValue = await File.ReadAllTextAsync(path, cancellationToken);
        return _protector.Unprotect(protectedValue);
    }

    /// <inheritdoc />
    public async Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        await File.WriteAllTextAsync(PathFor(key), _protector.Protect(value), cancellationToken);
    }

    /// <inheritdoc />
    public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        File.Delete(PathFor(key));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Maps a secret key to a safe file path by hashing it.
    /// </summary>
    /// <param name="key">The secret key.</param>
    /// <returns>The file path.</returns>
    private string PathFor(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_secretsDirectory, $"{hash}.secret");
    }
}
