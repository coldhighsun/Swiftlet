using Swiftlet.Domain.Accounts;

namespace Swiftlet.Application.Sync;

/// <summary>
/// Decides which accounts are due for a background synchronization, based on each account's interval.
/// </summary>
public sealed class SyncSchedule
{
    /// <summary>
    /// The time of the last synchronization attempt per account.
    /// </summary>
    private readonly Dictionary<Guid, DateTimeOffset> _lastAttempt = [];

    /// <summary>
    /// Gets the accounts whose interval has elapsed since their last attempt (or that were never attempted).
    /// </summary>
    /// <param name="accounts">The candidate accounts.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The due accounts.</returns>
    public IReadOnlyList<MailAccount> GetDue(IEnumerable<MailAccount> accounts, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        return accounts
            .Where(a => !_lastAttempt.TryGetValue(a.Id, out var last) || now - last >= a.SyncInterval)
            .ToList();
    }

    /// <summary>
    /// Records a synchronization attempt, successful or not, so failures are not retried in a tight loop.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="now">The time of the attempt.</param>
    public void MarkAttempted(Guid accountId, DateTimeOffset now)
    {
        _lastAttempt[accountId] = now;
    }
}
