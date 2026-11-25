namespace Swiftlet.Domain.Accounts;

/// <summary>
/// Authentication mechanism used to sign in to a mail server.
/// </summary>
public enum AuthMethod
{
    /// <summary>
    /// Password or app-specific password.
    /// </summary>
    Password = 0,

    /// <summary>
    /// OAuth 2.0 (XOAUTH2).
    /// </summary>
    OAuth2 = 1,
}
