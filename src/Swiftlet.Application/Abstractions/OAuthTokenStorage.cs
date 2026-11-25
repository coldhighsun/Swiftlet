using System.Text.Json;

namespace Swiftlet.Application.Abstractions;

/// <summary>
/// Persists OAuth 2.0 tokens of an account in the secret store.
/// </summary>
public static class OAuthTokenStorage
{
    /// <summary>
    /// Saves the tokens of an account.
    /// </summary>
    /// <param name="secrets">The secret store.</param>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="tokens">The tokens to save.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the tokens are stored.</returns>
    public static async Task SaveAsync(ISecretStore secrets, Guid accountId, OAuthTokens tokens, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(tokens);
        await secrets.SetAsync(AccountSecrets.OAuthKey(accountId), JsonSerializer.Serialize(tokens), cancellationToken);
    }

    /// <summary>
    /// Loads the tokens of an account.
    /// </summary>
    /// <param name="secrets">The secret store.</param>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The tokens, or <see langword="null"/> if none are stored.</returns>
    public static async Task<OAuthTokens?> LoadAsync(ISecretStore secrets, Guid accountId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        var json = await secrets.GetAsync(AccountSecrets.OAuthKey(accountId), cancellationToken);
        return json is null ? null : JsonSerializer.Deserialize<OAuthTokens>(json);
    }
}
