using System.Text.Json;
using Swiftlet.Application.Abstractions;

namespace Swiftlet.Application.Ai;

/// <summary>
/// Keeps one API key per provider in the secret store, bound to the destination it was entered for, so a key is never
/// sent to another host.
/// </summary>
/// <param name="secrets">The secret store.</param>
public sealed class AiKeyStore(ISecretStore secrets)
{
    /// <summary>
    /// The stored value.
    /// </summary>
    /// <param name="Destination">The destination the key belongs to.</param>
    /// <param name="Key">The API key.</param>
    private sealed record StoredKey(string Destination, string Key);

    /// <summary>
    /// Gets the name of the secret holding a provider's key.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <returns>The name.</returns>
    public static string SecretName(AiProvider provider)
    {
        return $"ai:apikey:{provider}";
    }

    /// <summary>
    /// Gets the key for a destination.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <param name="endpoint">The address about to be used.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The key, or <see langword="null"/> when none is stored for that destination.</returns>
    public async Task<string?> GetAsync(AiProvider provider, Uri endpoint, CancellationToken cancellationToken = default)
    {
        var text = await secrets.GetAsync(SecretName(provider), cancellationToken);
        if (text is null)
        {
            return null;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<StoredKey>(text);
            return stored?.Destination == AiEndpoint.Destination(provider, endpoint) ? stored.Key : null;
        }
        catch (JsonException)
        {
            // A damaged entry only means the key has to be typed again.
            return null;
        }
    }

    /// <summary>
    /// Stores the key of a destination, replacing the provider's previous one.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <param name="endpoint">The address the key belongs to.</param>
    /// <param name="key">The API key.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the key is stored.</returns>
    public Task SetAsync(AiProvider provider, Uri endpoint, string key, CancellationToken cancellationToken = default)
    {
        var stored = new StoredKey(AiEndpoint.Destination(provider, endpoint), key);
        return secrets.SetAsync(SecretName(provider), JsonSerializer.Serialize(stored), cancellationToken);
    }

    /// <summary>
    /// Removes a provider's key.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the key is removed.</returns>
    public Task RemoveAsync(AiProvider provider, CancellationToken cancellationToken = default)
    {
        return secrets.RemoveAsync(SecretName(provider), cancellationToken);
    }
}
