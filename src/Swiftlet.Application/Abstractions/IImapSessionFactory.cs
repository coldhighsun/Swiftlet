using Swiftlet.Domain.Accounts;

namespace Swiftlet.Application.Abstractions;

/// <summary>
/// Opens authenticated IMAP connections for accounts.
/// </summary>
public interface IImapSessionFactory
{
    /// <summary>
    /// Connects and authenticates to the IMAP server of an account.
    /// </summary>
    /// <param name="account">The account.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The open session; the caller disposes it.</returns>
    Task<IImapSession> ConnectAsync(MailAccount account, CancellationToken cancellationToken = default);

    /// <summary>
    /// Connects and authenticates with a password that is not stored yet, to verify it before it replaces the stored one.
    /// </summary>
    /// <param name="account">The password account.</param>
    /// <param name="password">The password to sign in with.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The open session; the caller disposes it.</returns>
    Task<IImapSession> ConnectAsync(MailAccount account, string password, CancellationToken cancellationToken = default);
}
