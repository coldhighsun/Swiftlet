using Microsoft.Extensions.Logging;
using Swiftlet.Application.Abstractions;

namespace Swiftlet.Infrastructure.Security;

/// <summary>
/// Picks the best available secret store of the current operating system.
/// </summary>
public static class PlatformSecretStore
{
    /// <summary>
    /// Creates the secret store for the current platform: DPAPI on Windows, the login keychain on macOS and the Secret Service on Linux,
    /// falling back to an encrypted file store when no system store is usable.
    /// </summary>
    /// <param name="dataDirectory">The application data directory used for file-based stores.</param>
    /// <param name="logger">The logger.</param>
    /// <returns>The secret store.</returns>
    public static ISecretStore Create(string dataDirectory, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(logger);
        return Create(dataDirectory, logger, new ProcessCommandRunner());
    }

    /// <summary>
    /// Creates the secret store for the current platform with a specific command runner.
    /// </summary>
    /// <param name="dataDirectory">The application data directory used for file-based stores.</param>
    /// <param name="logger">The logger.</param>
    /// <param name="runner">The command runner used by the keychain stores.</param>
    /// <returns>The secret store.</returns>
    internal static ISecretStore Create(string dataDirectory, ILogger logger, ICommandRunner runner)
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsSecretStore(Path.Combine(dataDirectory, "secrets-dpapi"));
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacKeychainSecretStore(runner);
        }

        if (OperatingSystem.IsLinux() && LibSecretStore.IsAvailableAsync(runner).GetAwaiter().GetResult())
        {
            return new LibSecretStore(runner);
        }

        logger.LogWarning(
            "No system keychain is available (install libsecret-tools and run a Secret Service); "
            + "secrets are stored in an application data file whose key is not protected by the system.");
        return new DataProtectionSecretStore(dataDirectory);
    }
}
