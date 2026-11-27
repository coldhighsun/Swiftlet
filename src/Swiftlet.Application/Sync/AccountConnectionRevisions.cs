using System.Collections.Concurrent;

namespace Swiftlet.Application.Sync;

/// <summary>
/// Counts the changes of each account's connection settings (IMAP server or password), so that background services
/// holding a connection or waiting out a failure notice that they work with outdated settings.
/// </summary>
/// <remarks>
/// Read the revision before reading the account: a change in between then only causes one needless reconnect.
/// </remarks>
public sealed class AccountConnectionRevisions
{
    /// <summary>
    /// The number of changes per account since the application started.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, int> _revisions = new();

    /// <summary>
    /// Gets the current revision of an account.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <returns>The revision; zero until the settings change.</returns>
    public int Get(Guid accountId)
    {
        return _revisions.GetValueOrDefault(accountId);
    }

    /// <summary>
    /// Records that the connection settings of an account changed; call it after the new settings are stored.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    public void Bump(Guid accountId)
    {
        _revisions.AddOrUpdate(accountId, 1, static (_, revision) => revision + 1);
    }
}
