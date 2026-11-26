using System.Text;
using System.Text.Json;
using Swiftlet.Application.Abstractions;
using Swiftlet.Domain.Accounts;
using Swiftlet.Infrastructure.Resilience;

namespace Swiftlet.Infrastructure.OAuth;

/// <summary>
/// The token endpoint answered with an OAuth error.
/// </summary>
public sealed class OAuthTokenException : OAuthException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OAuthTokenException"/> class.
    /// </summary>
    /// <param name="error">The OAuth error code, for example <c>invalid_grant</c>.</param>
    /// <param name="description">The error description from the provider, if any.</param>
    public OAuthTokenException(string error, string? description)
        : base(string.IsNullOrWhiteSpace(description) ? error : $"{error}: {description}")
    {
        Error = error;
    }

    /// <summary>
    /// Gets the OAuth error code.
    /// </summary>
    public string Error { get; }
}

/// <summary>
/// A successful answer of the token endpoint.
/// </summary>
/// <param name="AccessToken">The access token.</param>
/// <param name="RefreshToken">The refresh token, if the provider issued a new one.</param>
/// <param name="ExpiresAt">The time the access token expires.</param>
/// <param name="IdToken">The OpenID Connect ID token, if requested.</param>
internal sealed record TokenResponse(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt, string? IdToken);

/// <summary>
/// Talks to the token endpoints of the identity providers.
/// </summary>
/// <param name="http">The HTTP client.</param>
/// <param name="configuration">The client registrations.</param>
/// <param name="timeProvider">The clock.</param>
internal sealed class OAuthTokenClient(HttpClient http, OAuthClientConfiguration configuration, TimeProvider timeProvider)
{
    /// <summary>
    /// How many times a token refresh that failed with a passing error is repeated.
    /// </summary>
    private const int RefreshRetries = 3;

    /// <summary>
    /// Repeats a token refresh after network errors and busy answers. A code exchange never goes through it: a code works once.
    /// </summary>
    private readonly Polly.ResiliencePipeline _refreshRetry =
        RetryPipelines.Transient(timeProvider, RefreshRetries, RetryPipelines.TokenRefreshDelay, null, "Token refresh");

    /// <summary>
    /// Exchanges an authorization code for tokens.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <param name="code">The authorization code.</param>
    /// <param name="codeVerifier">The PKCE code verifier.</param>
    /// <param name="redirectUri">The redirect address used in the authorization request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The issued tokens.</returns>
    public async Task<TokenResponse> ExchangeCodeAsync(
        OAuthProvider provider,
        string code,
        string codeVerifier,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        return await PostAsync(
            provider,
            new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["code_verifier"] = codeVerifier,
                ["redirect_uri"] = redirectUri,
            },
            cancellationToken);
    }

    /// <summary>
    /// Obtains a new access token with a refresh token.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <param name="refreshToken">The refresh token.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The refreshed tokens.</returns>
    public async Task<TokenResponse> RefreshAsync(OAuthProvider provider, string refreshToken, CancellationToken cancellationToken)
    {
        return await _refreshRetry.ExecuteAsync(
            async token => await PostAsync(
                provider,
                new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refreshToken,
                },
                token),
            cancellationToken);
    }

    /// <summary>
    /// Posts a token request and parses the response.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <param name="form">The grant-specific form fields.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The parsed response.</returns>
    private async Task<TokenResponse> PostAsync(OAuthProvider provider, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        var clientId = configuration.ClientIdFor(provider)
            ?? throw new InvalidOperationException($"No client id is configured for {provider}.");
        form["client_id"] = clientId;
        if (configuration.ClientSecretFor(provider) is { } secret)
        {
            form["client_secret"] = secret;
        }

        var endpoints = OAuthProviderEndpoints.For(provider);
        using var content = new FormUrlEncodedContent(form);
        using var response = await http.PostAsync(endpoints.TokenEndpoint, content, cancellationToken);
        if (TransientErrors.IsTransient(response.StatusCode))
        {
            // Checked before parsing: a busy gateway often answers with a page that is not JSON.
            throw new HttpRequestException(
                $"The token endpoint answered {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (!response.IsSuccessStatusCode || root.TryGetProperty("error", out _))
        {
            var error = root.TryGetProperty("error", out var e) ? e.GetString() : null;
            var description = root.TryGetProperty("error_description", out var d) ? d.GetString() : null;
            throw new OAuthTokenException(error ?? $"http_{(int)response.StatusCode}", description);
        }

        var accessToken = root.GetProperty("access_token").GetString()
            ?? throw new OAuthException("The token endpoint returned no access token.");
        var expiresIn = root.TryGetProperty("expires_in", out var seconds) ? seconds.GetInt32() : 3600;
        return new TokenResponse(
            accessToken,
            root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null,
            timeProvider.GetUtcNow().AddSeconds(expiresIn),
            root.TryGetProperty("id_token", out var id) ? id.GetString() : null);
    }

    /// <summary>
    /// Reads the email address and display name from an OpenID Connect ID token.
    /// The token was received directly from the token endpoint over TLS, so its signature is not re-verified.
    /// </summary>
    /// <param name="idToken">The ID token (a JWT).</param>
    /// <returns>The email address and display name; either may be <see langword="null"/>.</returns>
    public static (string? Email, string? Name) ReadIdentity(string? idToken)
    {
        var parts = idToken?.Split('.');
        if (parts is not { Length: 3 })
        {
            return (null, null);
        }

        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
        var root = document.RootElement;
        var email = root.TryGetProperty("email", out var e) ? e.GetString()
            : root.TryGetProperty("preferred_username", out var u) ? u.GetString()
            : null;
        return (email, root.TryGetProperty("name", out var n) ? n.GetString() : null);
    }
}
