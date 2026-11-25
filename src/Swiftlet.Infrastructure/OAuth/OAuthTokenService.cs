using System.Collections.Concurrent;
using Swiftlet.Application.Abstractions;
using Swiftlet.Domain.Accounts;

namespace Swiftlet.Infrastructure.OAuth;

/// <summary>
/// Hands out valid access tokens and refreshes them shortly before they expire.
/// </summary>
/// <param name="secrets">The secret store holding the tokens.</param>
/// <param name="tokenClient">The token endpoint client.</param>
/// <param name="timeProvider">The clock.</param>
internal sealed class OAuthTokenService(ISecretStore secrets, OAuthTokenClient tokenClient, TimeProvider timeProvider) : IAccessTokenProvider
{
    /// <summary>
    /// Tokens expiring within this window are refreshed first, so they do not expire mid-request.
    /// </summary>
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(2);

    /// <summary>
    /// One lock per account so concurrent callers trigger a single refresh.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _gates = new();

    /// <inheritdoc />
    public async Task<string> GetAccessTokenAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (account.AuthMethod != AuthMethod.OAuth2 || account.OAuthProvider is not { } provider)
        {
            throw new InvalidOperationException($"Account {account.EmailAddress} does not use OAuth 2.0.");
        }

        var gate = _gates.GetOrAdd(account.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var tokens = await OAuthTokenStorage.LoadAsync(secrets, account.Id, cancellationToken)
                ?? throw new OAuthReauthenticationRequiredException(account.EmailAddress);
            if (tokens.ExpiresAt - timeProvider.GetUtcNow() > RefreshSkew)
            {
                return tokens.AccessToken;
            }

            TokenResponse refreshed;
            try
            {
                refreshed = await tokenClient.RefreshAsync(provider, tokens.RefreshToken, cancellationToken);
            }
            catch (OAuthTokenException ex) when (ex.Error == "invalid_grant")
            {
                // The refresh token was revoked or expired; only a new interactive sign-in can fix this.
                throw new OAuthReauthenticationRequiredException(account.EmailAddress);
            }

            // Microsoft rotates refresh tokens; Google keeps the old one unless a new one is returned.
            var updated = new OAuthTokens(
                refreshed.AccessToken,
                string.IsNullOrEmpty(refreshed.RefreshToken) ? tokens.RefreshToken : refreshed.RefreshToken,
                refreshed.ExpiresAt);
            await OAuthTokenStorage.SaveAsync(secrets, account.Id, updated, cancellationToken);
            return updated.AccessToken;
        }
        finally
        {
            gate.Release();
        }
    }
}
