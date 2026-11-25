namespace Swiftlet.Application.Abstractions;

/// <summary>
/// Naming of the secrets stored for an account.
/// </summary>
public static class AccountSecrets
{
    /// <summary>
    /// Gets the secret key holding the password (or app-specific password) of an account.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <returns>The secret key.</returns>
    public static string PasswordKey(Guid accountId)
    {
        return $"account:{accountId:N}:password";
    }

    /// <summary>
    /// Gets the secret key holding the OAuth 2.0 tokens of an account.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <returns>The secret key.</returns>
    public static string OAuthKey(Guid accountId)
    {
        return $"account:{accountId:N}:oauth";
    }
}
