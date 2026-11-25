using System.Text;
using Swiftlet.Application.Abstractions;

namespace Swiftlet.Infrastructure.Security;

/// <summary>
/// Stores secrets as generic passwords in the macOS login keychain through the <c>security</c> command-line tool.
/// Values are Base64-encoded because <c>security -w</c> prints some values (non-ASCII, control characters) in hex.
/// The value is passed as a command-line argument when writing, so it is briefly visible to processes of the same user.
/// </summary>
/// <param name="runner">The command runner.</param>
internal sealed class MacKeychainSecretStore(ICommandRunner runner) : ISecretStore
{
    /// <summary>
    /// The keychain service name all secrets are filed under.
    /// </summary>
    public const string Service = "Swiftlet";

    /// <summary>
    /// The exit code of <c>security</c> when an item does not exist.
    /// </summary>
    private const int ItemNotFound = 44;

    /// <inheritdoc />
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync(
            "security",
            ["find-generic-password", "-s", Service, "-a", key, "-w"],
            null,
            cancellationToken);
        if (result.ExitCode == ItemNotFound)
        {
            return null;
        }

        EnsureSuccess(result, "read");
        var encoded = result.StandardOutput.TrimEnd('\r', '\n');
        return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
    }

    /// <inheritdoc />
    public async Task SetAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

        // -U updates the item if it already exists.
        var result = await runner.RunAsync(
            "security",
            ["add-generic-password", "-U", "-s", Service, "-a", key, "-w", encoded],
            null,
            cancellationToken);
        EnsureSuccess(result, "write");
    }

    /// <inheritdoc />
    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync(
            "security",
            ["delete-generic-password", "-s", Service, "-a", key],
            null,
            cancellationToken);
        if (result.ExitCode != ItemNotFound)
        {
            EnsureSuccess(result, "delete");
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
            throw new InvalidOperationException($"Could not {action} the keychain item (exit code {result.ExitCode}): {result.StandardError.Trim()}");
        }
    }
}
