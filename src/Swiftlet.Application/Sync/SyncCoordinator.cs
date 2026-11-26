using System.Collections.Concurrent;

namespace Swiftlet.Application.Sync;

/// <summary>
/// Serializes synchronization work per account: callers for the same account wait for each other, different accounts run concurrently.
/// </summary>
public sealed class SyncCoordinator
{
    /// <summary>
    /// The per-account locks.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    /// <summary>
    /// The number of callers waiting for each account's lock.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, int> _waiting = new();

    /// <summary>
    /// Tells whether anyone is waiting for an account's lock, so a long-running holder can finish early.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <returns><see langword="true"/> if at least one caller is waiting.</returns>
    public bool HasWaiters(Guid accountId)
    {
        return _waiting.GetValueOrDefault(accountId) > 0;
    }

    /// <summary>
    /// Runs the action once no other action for the same account is running; waits rather than skips.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="action">The work to run exclusively.</param>
    /// <param name="cancellationToken">Cancels the wait for the lock; the action itself is responsible for its own cancellation.</param>
    /// <returns>The action's result.</returns>
    public async Task<T> RunExclusiveAsync<T>(Guid accountId, Func<Task<T>> action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        var gate = _locks.GetOrAdd(accountId, static _ => new SemaphoreSlim(1, 1));
        _waiting.AddOrUpdate(accountId, 1, static (_, count) => count + 1);
        try
        {
            await gate.WaitAsync(cancellationToken);
        }
        finally
        {
            _waiting.AddOrUpdate(accountId, 0, static (_, count) => count - 1);
        }

        try
        {
            return await action();
        }
        finally
        {
            gate.Release();
        }
    }
}
