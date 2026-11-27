using System.Diagnostics.CodeAnalysis;

namespace Swiftlet.Application.Ai;

/// <summary>
/// The rules for the address of an AI service.
/// </summary>
public static class AiEndpoint
{
    /// <summary>
    /// Gets the address suggested for a provider.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <returns>The address; for compatible services the one of a local Ollama.</returns>
    public static string DefaultFor(AiProvider provider)
    {
        return provider == AiProvider.Claude ? "https://api.anthropic.com" : "http://localhost:11434/v1";
    }

    /// <summary>
    /// Reads an address. Mail content and the API key travel to it, so it must use https unless it points at this
    /// computer, and it carries no user name, query or fragment.
    /// </summary>
    /// <param name="text">The typed address.</param>
    /// <param name="endpoint">The address when it is acceptable.</param>
    /// <returns>True if the address is acceptable.</returns>
    public static bool TryParse(string? text, [NotNullWhen(true)] out Uri? endpoint)
    {
        endpoint = null;
        if (!Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri)
            || uri.Host.Length == 0
            || uri.UserInfo.Length > 0
            || uri.Query.Length > 0
            || uri.Fragment.Length > 0)
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        {
            return false;
        }

        endpoint = uri;
        return true;
    }

    /// <summary>
    /// Identifies who receives a request: the provider and the scheme, host and port of the address. Consent and the
    /// stored API key are bound to it.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <param name="endpoint">The address.</param>
    /// <returns>The identifier.</returns>
    public static string Destination(AiProvider provider, Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return $"{provider}|{endpoint.GetLeftPart(UriPartial.Authority).ToLowerInvariant()}";
    }
}
