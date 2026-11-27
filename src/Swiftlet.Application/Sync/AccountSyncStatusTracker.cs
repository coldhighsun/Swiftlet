using System.Collections.Concurrent;
using Swiftlet.Application.Abstractions;

namespace Swiftlet.Application.Sync;

/// <summary>
/// How the synchronization of an account has gone since the application started.
/// </summary>
/// <param name="IsSyncing">Whether a sync is running now.</param>
/// <param name="LastSucceededAt">When the last successful sync ended, or <see langword="null"/> if none has.</param>
/// <param name="FailedFolders">The folders the last successful sync could not synchronize.</param>
/// <param name="LastFailedAt">When the last sync failed, or <see langword="null"/> if the latest finished one succeeded.</param>
/// <param name="Error">Why that sync failed, or <see langword="null"/>.</param>
/// <param name="RequiresSignIn">Whether it failed because the account has to be signed in again.</param>
public sealed record AccountSyncStatus(
    bool IsSyncing,
    DateTimeOffset? LastSucceededAt,
    IReadOnlyList<string> FailedFolders,
    DateTimeOffset? LastFailedAt,
    string? Error,
    bool RequiresSignIn)
{
    /// <summary>
    /// Gets the status of an account that has not been synchronized yet.
    /// </summary>
    public static AccountSyncStatus Unknown { get; } = new(false, null, [], null, null, false);
}

/// <summary>
/// Remembers the synchronization status of every account. It is kept in memory only: every account is synchronized
/// shortly after the start, which fills it again.
/// </summary>
/// <param name="timeProvider">The clock.</param>
public sealed class AccountSyncStatusTracker(TimeProvider timeProvider)
{
    /// <summary>
    /// The status per account.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, AccountSyncStatus> _statuses = new();

    /// <summary>
    /// Gets the status of an account.
    /// </summary>
    /// <param name="accountId">The account.</param>
    /// <returns>The status; <see cref="AccountSyncStatus.Unknown"/> if nothing was recorded.</returns>
    public AccountSyncStatus Get(Guid accountId)
    {
        return _statuses.GetValueOrDefault(accountId, AccountSyncStatus.Unknown);
    }

    /// <summary>
    /// Records that a sync started; the outcome of the previous one stays until this one ends.
    /// </summary>
    /// <param name="accountId">The account.</param>
    public void Started(Guid accountId)
    {
        Change(accountId, s => s with { IsSyncing = true });
    }

    /// <summary>
    /// Records a sync that ended successfully, possibly leaving some folders out.
    /// </summary>
    /// <param name="accountId">The account.</param>
    /// <param name="failedFolders">The folders that failed.</param>
    public void Succeeded(Guid accountId, IReadOnlyList<string> failedFolders)
    {
        var now = timeProvider.GetUtcNow();
        Change(accountId, _ => new AccountSyncStatus(false, now, failedFolders, null, null, false));
    }

    /// <summary>
    /// Records a failed sync; the time of the last successful one is kept.
    /// </summary>
    /// <param name="accountId">The account.</param>
    /// <param name="exception">Why it failed.</param>
    public void Failed(Guid accountId, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var now = timeProvider.GetUtcNow();
        Change(accountId, s => new AccountSyncStatus(
            false, s.LastSucceededAt, [], now, exception.Message, exception is OAuthReauthenticationRequiredException));
    }

    /// <summary>
    /// Records that a sync was abandoned, which says nothing about the account.
    /// </summary>
    /// <param name="accountId">The account.</param>
    public void Cancelled(Guid accountId)
    {
        Change(accountId, s => s with { IsSyncing = false });
    }

    /// <summary>
    /// Replaces the status of an account.
    /// </summary>
    /// <param name="accountId">The account.</param>
    /// <param name="change">Produces the new status from the current one.</param>
    private void Change(Guid accountId, Func<AccountSyncStatus, AccountSyncStatus> change)
    {
        _statuses.AddOrUpdate(accountId, _ => change(AccountSyncStatus.Unknown), (_, current) => change(current));
    }
}
