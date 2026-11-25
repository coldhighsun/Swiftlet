using System.Collections.ObjectModel;
using Swiftlet.Domain.Accounts;

namespace Swiftlet.Infrastructure.OAuth;

/// <summary>
/// The endpoints, scopes and quirks of an OAuth 2.0 identity provider.
/// </summary>
/// <param name="AuthorizationEndpoint">The authorization (sign-in page) endpoint.</param>
/// <param name="TokenEndpoint">The token endpoint.</param>
/// <param name="Scope">The space-separated scopes requested.</param>
/// <param name="RedirectHost">The loopback host name used in the redirect address.</param>
/// <param name="ExtraAuthorizationParameters">Provider-specific authorization request parameters.</param>
internal sealed record OAuthProviderEndpoints(
    Uri AuthorizationEndpoint,
    Uri TokenEndpoint,
    string Scope,
    string RedirectHost,
    IReadOnlyDictionary<string, string> ExtraAuthorizationParameters)
{
    /// <summary>
    /// Gets the endpoints of a provider.
    /// </summary>
    /// <param name="provider">The provider.</param>
    /// <returns>The endpoints.</returns>
    public static OAuthProviderEndpoints For(OAuthProvider provider)
    {
        return provider switch
        {
            OAuthProvider.Google => new OAuthProviderEndpoints(
                new Uri("https://accounts.google.com/o/oauth2/v2/auth"),
                new Uri("https://oauth2.googleapis.com/token"),
                "https://mail.google.com/ openid email profile",
                "127.0.0.1",
                new ReadOnlyDictionary<string, string>(new Dictionary<string, string>
                {
                    // "offline" + "consent" make Google return a refresh token.
                    ["access_type"] = "offline",
                    ["prompt"] = "consent",
                })),
            OAuthProvider.Microsoft => new OAuthProviderEndpoints(
                new Uri("https://login.microsoftonline.com/common/oauth2/v2.0/authorize"),
                new Uri("https://login.microsoftonline.com/common/oauth2/v2.0/token"),
                "https://outlook.office.com/IMAP.AccessAsUser.All https://outlook.office.com/SMTP.Send offline_access openid email profile",

                // Azure only accepts "localhost" for loopback redirects of public clients registered in the portal.
                "localhost",
                new ReadOnlyDictionary<string, string>(new Dictionary<string, string>
                {
                    ["prompt"] = "select_account",
                })),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null),
        };
    }
}
