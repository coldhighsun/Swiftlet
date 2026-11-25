using System.Globalization;
using Swiftlet.Application.Abstractions;
using Swiftlet.Domain.Accounts;

namespace Swiftlet.Infrastructure.OAuth;

/// <summary>
/// Interactive OAuth 2.0 authorization-code sign-in with PKCE and a loopback redirect.
/// </summary>
/// <param name="tokenClient">The token endpoint client.</param>
/// <param name="browser">The browser launcher.</param>
/// <param name="configuration">The client registrations.</param>
/// <param name="settingsPath">The settings file path, used in the "not configured" hint.</param>
internal sealed class OAuthAuthorizer(
    OAuthTokenClient tokenClient,
    IBrowserLauncher browser,
    OAuthClientConfiguration configuration,
    string settingsPath) : IOAuthAuthorizer
{
    /// <summary>
    /// How long the user may take to finish signing in.
    /// </summary>
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);

    /// <inheritdoc />
    public async Task<OAuthAuthorization> AuthorizeAsync(OAuthProvider provider, CancellationToken cancellationToken = default)
    {
        var clientId = configuration.ClientIdFor(provider)
            ?? throw new OAuthNotConfiguredException(provider, OAuthClientConfiguration.ConfigurationHint(settingsPath));
        var endpoints = OAuthProviderEndpoints.For(provider);

        var verifier = Pkce.CreateRandomString(64);
        var state = Pkce.CreateRandomString();
        using var server = LoopbackCallbackServer.Start();
        var redirectUri = string.Create(CultureInfo.InvariantCulture, $"http://{endpoints.RedirectHost}:{server.Port}/");

        var parameters = new Dictionary<string, string>(endpoints.ExtraAuthorizationParameters)
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = endpoints.Scope,
            ["state"] = state,
            ["code_challenge"] = Pkce.CreateChallenge(verifier),
            ["code_challenge_method"] = "S256",
        };
        var query = string.Join('&', parameters.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
        var authorizationUri = new UriBuilder(endpoints.AuthorizationEndpoint) { Query = query }.Uri;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(SignInTimeout);
        await browser.OpenAsync(authorizationUri, cancellationToken);

        IReadOnlyDictionary<string, string> callback;
        try
        {
            callback = await server.WaitForCallbackAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OAuthException("Timed out waiting for the browser sign-in to finish.");
        }

        if (callback.TryGetValue("error", out var error))
        {
            callback.TryGetValue("error_description", out var description);
            throw new OAuthException(string.IsNullOrWhiteSpace(description) ? error : $"{error}: {description}");
        }

        if (!callback.TryGetValue("state", out var returnedState) || returnedState != state)
        {
            throw new OAuthException("The sign-in response did not match the request (state mismatch).");
        }

        var tokens = await tokenClient.ExchangeCodeAsync(provider, callback["code"], verifier, redirectUri, cancellationToken);
        if (string.IsNullOrEmpty(tokens.RefreshToken))
        {
            throw new OAuthException("The provider did not return a refresh token, so the account cannot stay signed in.");
        }

        var (email, name) = OAuthTokenClient.ReadIdentity(tokens.IdToken);
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new OAuthException("The provider did not report the email address of the signed-in user.");
        }

        return new OAuthAuthorization(email, name, new OAuthTokens(tokens.AccessToken, tokens.RefreshToken, tokens.ExpiresAt));
    }
}
