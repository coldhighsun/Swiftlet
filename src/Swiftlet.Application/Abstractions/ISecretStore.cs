namespace Swiftlet.Application.Abstractions;

/// <summary>
/// Secure storage for secrets such as mailbox passwords and OAuth tokens.
/// </summary>
public interface ISecretStore
{
    /// <summary>
    /// Gets a secret.
    /// </summary>
    /// <param name="key">The secret key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The secret value, or <see langword="null"/> if not stored.</returns>
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores or replaces a secret.
    /// </summary>
    /// <param name="key">The secret key.</param>
    /// <param name="value">The secret value.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the secret is stored.</returns>
    Task SetAsync(string key, string value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes a secret if it exists.
    /// </summary>
    /// <param name="key">The secret key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the secret is removed.</returns>
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
}
