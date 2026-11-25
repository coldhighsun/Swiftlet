using Swiftlet.Domain.Accounts;

namespace Swiftlet.Application.Abstractions;

/// <summary>
/// OAuth 2.0 tokens of an account.
/// </summary>
/// <param name="AccessToken">The short-lived access token.</param>
/// <param name="RefreshToken">The long-lived refresh token.</param>
/// <param name="ExpiresAt">The time the access token expires.</param>
public sealed record OAuthTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>
/// The outcome of a successful interactive sign-in.
/// </summary>
/// <param name="EmailAddress">The email address of the signed-in user.</param>
/// <param name="DisplayName">The user's display name, if the provider reported one.</param>
/// <param name="Tokens">The issued tokens.</param>
public sealed record OAuthAuthorization(string EmailAddress, string? DisplayName, OAuthTokens Tokens);

/// <summary>
/// Runs the interactive OAuth 2.0 sign-in in the user's browser.
/// </summary>
public interface IOAuthAuthorizer
{
    /// <summary>
    /// Opens the provider's sign-in page in the system browser and waits for the user to finish.
    /// </summary>
    /// <param name="provider">The identity provider.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The signed-in user and tokens.</returns>
    Task<OAuthAuthorization> AuthorizeAsync(OAuthProvider provider, CancellationToken cancellationToken = default);
}

/// <summary>
/// Provides valid OAuth 2.0 access tokens for accounts, refreshing them when they are about to expire.
/// </summary>
public interface IAccessTokenProvider
{
    /// <summary>
    /// Gets a valid access token for an OAuth 2.0 account.
    /// </summary>
    /// <param name="account">The account.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The access token.</returns>
    Task<string> GetAccessTokenAsync(MailAccount account, CancellationToken cancellationToken = default);
}

/// <summary>
/// Opens web pages in the user's default browser.
/// </summary>
public interface IBrowserLauncher
{
    /// <summary>
    /// Opens an http or https address.
    /// </summary>
    /// <param name="uri">The address to open.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the browser has been asked to open the page.</returns>
    Task OpenAsync(Uri uri, CancellationToken cancellationToken = default);
}

/// <summary>
/// An OAuth 2.0 sign-in or token operation failed.
/// </summary>
public class OAuthException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OAuthException"/> class.
    /// </summary>
    public OAuthException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="OAuthException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    public OAuthException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="OAuthException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The underlying exception.</param>
    public OAuthException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Sign-in with a provider is not possible because no client id has been configured for it.
/// </summary>
public sealed class OAuthNotConfiguredException : OAuthException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OAuthNotConfiguredException"/> class.
    /// </summary>
    /// <param name="provider">The provider without a client id.</param>
    /// <param name="hint">How to configure the client id.</param>
    public OAuthNotConfiguredException(OAuthProvider provider, string hint)
        : base($"OAuth sign-in with {provider} is not configured. {hint}")
    {
        Provider = provider;
        Hint = hint;
    }

    /// <summary>
    /// Gets the provider without a client id.
    /// </summary>
    public OAuthProvider Provider { get; }

    /// <summary>
    /// Gets how to configure the client id.
    /// </summary>
    public string Hint { get; }
}

/// <summary>
/// The stored tokens are no longer valid and the user must sign in again.
/// </summary>
public sealed class OAuthReauthenticationRequiredException : OAuthException
{
    /// <summary>
    /// Initializes a new instance of the <see cref="OAuthReauthenticationRequiredException"/> class.
    /// </summary>
    /// <param name="emailAddress">The account that needs to sign in again.</param>
    public OAuthReauthenticationRequiredException(string emailAddress)
        : base($"The sign-in of {emailAddress} has expired or was revoked; please sign in again.")
    {
        EmailAddress = emailAddress;
    }

    /// <summary>
    /// Gets the account that needs to sign in again.
    /// </summary>
    public string EmailAddress { get; }
}
