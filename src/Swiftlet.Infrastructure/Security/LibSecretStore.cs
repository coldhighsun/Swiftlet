using System.ComponentModel;
using Swiftlet.Application.Abstractions;

namespace Swiftlet.Infrastructure.Security;

/// <summary>
/// Stores secrets in the Secret Service (GNOME Keyring, KWallet, ...) through the <c>secret-tool</c> command from libsecret.
/// The secret itself travels over standard input and never appears in the process list.
/// </summary>
/// <param name="runner">The command runner.</param>
internal sealed class LibSecretStore(ICommandRunner runner) : ISecretStore
{
    /// <summary>
    /// The attribute value all secrets are filed under.
    /// </summary>
    public const string Service = "Swiftlet";

    /// <summary>
    /// Determines whether <c>secret-tool</c> is installed and a Secret Service is reachable.
    /// </summary>
    /// <param name="runner">The command runner.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><see langword="true"/> if the store can be used.</returns>
    public static async Task<bool> IsAvailableAsync(ICommandRunner runner, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        try
        {
            // Looking up a non-existent item succeeds silently (exit code 1, no output) when a Secret Service is running.
            var result = await runner.RunAsync(
                "secret-tool",
                ["lookup", "service", Service, "account", "availability-probe"],
                null,
                cancellationToken);
            return string.IsNullOrWhiteSpace(result.StandardError);
        }
        catch (Win32Exception)
        {
            // secret-tool is not installed.
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync("secret-tool", ["lookup", "service", Service, "account", key], null, cancellationToken);
        if (result.ExitCode != 0)
        {
            // secret-tool reports "not found" as exit code 1 with no error text.
            return string.IsNullOrWhiteSpace(result.StandardError)
                ? null
                : throw new InvalidOperationException($"Could not read the secret: {result.StandardError.Trim()}");
        }

        return result.StandardOutput;
    }

    /// <inheritdoc />
    public async Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        var result = await runner.RunAsync(
            "secret-tool",
            ["store", "--label=Swiftlet", "service", Service, "account", key],
            value,
            cancellationToken);
        EnsureSuccess(result, "store");
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync("secret-tool", ["clear", "service", Service, "account", key], null, cancellationToken);
        if (result.ExitCode != 0 && !string.IsNullOrWhiteSpace(result.StandardError))
        {
            throw new InvalidOperationException($"Could not delete the secret: {result.StandardError.Trim()}");
        }
    }

    /// <summary>
    /// Throws when a command failed.
    /// </summary>
    /// <param name="result">The command result.</param>
    /// <param name="action">The attempted action, for the message.</param>
    private static void EnsureSuccess(CommandResult result, string action)
    {
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Could not {action} the secret (exit code {result.ExitCode}): {result.StandardError.Trim()}");
        }
    }
}
