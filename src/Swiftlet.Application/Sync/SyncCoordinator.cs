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
        await gate.WaitAsync(cancellationToken);
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
