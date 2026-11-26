namespace Swiftlet.Application.Sync;

/// <summary>
/// Serializes the flag work of each folder between the regular sync and the immediate flag push.
/// It is separate from <see cref="SyncCoordinator"/> and held only briefly, so a read or star change does not wait for a whole sync.
/// </summary>
public sealed class FlagCoordinator
{
    /// <summary>
    /// The per-folder locks.
    /// </summary>
    private readonly SyncCoordinator _locks = new();

    /// <summary>
    /// Runs the action once no other flag work of the same folder is running.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="folderId">The folder identifier.</param>
    /// <param name="action">The work to run exclusively.</param>
    /// <param name="cancellationToken">Cancels the wait for the lock.</param>
    /// <returns>The action's result.</returns>
    public Task<T> RunExclusiveAsync<T>(Guid folderId, Func<Task<T>> action, CancellationToken cancellationToken = default)
    {
        return _locks.RunExclusiveAsync(folderId, action, cancellationToken);
    }
}
