namespace Swiftlet.Domain.Accounts;

/// <summary>
/// The identity provider issuing OAuth 2.0 tokens for a mail account.
/// </summary>
public enum OAuthProvider
{
    /// <summary>
    /// Google (Gmail and Google Workspace).
    /// </summary>
    Google = 0,

    /// <summary>
    /// Microsoft (Outlook.com and Microsoft 365).
    /// </summary>
    Microsoft = 1,
}
