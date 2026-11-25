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
}
