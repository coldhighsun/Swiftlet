namespace Swiftlet.Application.Sync;

/// <summary>
/// Event data raised after an account finished a synchronization.
/// </summary>
/// <param name="accountId">The account identifier.</param>
/// <param name="newMessages">The number of newly cached messages.</param>
public sealed class AccountSyncedEventArgs(Guid accountId, int newMessages) : EventArgs
{
    /// <summary>
    /// Gets the account identifier.
    /// </summary>
    public Guid AccountId { get; } = accountId;

    /// <summary>
    /// Gets the number of newly cached messages.
    /// </summary>
    public int NewMessages { get; } = newMessages;
}

/// <summary>
/// Event data raised while the history of an account is downloaded in the background.
/// </summary>
/// <param name="accountId">The account identifier.</param>
/// <param name="downloaded">The number of history messages downloaded so far in the current pass.</param>
/// <param name="finished">Whether the pass is over (everything downloaded, or the pass ended early).</param>
/// <param name="folderName">The folder being downloaded, or <see langword="null"/> when the report is not about a folder (for example the final one).</param>
/// <param name="remaining">How many older messages the folder still holds on the server.</param>
public sealed class BackfillProgressEventArgs(Guid accountId, int downloaded, bool finished, string? folderName = null, int remaining = 0) : EventArgs
{
    /// <summary>
    /// Gets the account identifier.
    /// </summary>
    public Guid AccountId { get; } = accountId;

    /// <summary>
    /// Gets the number of history messages downloaded so far in the current pass.
    /// </summary>
    public int Downloaded { get; } = downloaded;

    /// <summary>
    /// Gets a value indicating whether the pass is over.
    /// </summary>
    public bool Finished { get; } = finished;

    /// <summary>
    /// Gets the full name of the folder being downloaded, or <see langword="null"/> when the report is not about a folder.
    /// </summary>
    public string? FolderName { get; } = folderName;

    /// <summary>
    /// Gets how many older messages the folder still holds on the server.
    /// </summary>
    public int Remaining { get; } = remaining;
}

/// <summary>
/// Notifies subscribers about completed synchronizations.
/// </summary>
public interface ISyncEvents
{
    /// <summary>
    /// Raised after an account was synchronized. May be raised on a background thread.
    /// </summary>
    event EventHandler<AccountSyncedEventArgs>? AccountSynced;

    /// <summary>
    /// Raised after every message the background history download stores and when a history pass ends. May be raised on a background thread.
    /// </summary>
    event EventHandler<BackfillProgressEventArgs>? BackfillProgressed;
}

/// <summary>
/// The default <see cref="ISyncEvents"/> implementation, which the sync handler raises events on.
/// </summary>
public sealed class SyncEvents : ISyncEvents
{
    /// <inheritdoc />
    public event EventHandler<AccountSyncedEventArgs>? AccountSynced;

    /// <inheritdoc />
    public event EventHandler<BackfillProgressEventArgs>? BackfillProgressed;

    /// <summary>
    /// Raises <see cref="BackfillProgressed"/>; a failing subscriber never affects the backfill or the other subscribers.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="downloaded">The number of history messages downloaded so far in the current pass.</param>
    /// <param name="finished">Whether the pass is over.</param>
    /// <param name="folderName">The folder being downloaded, if the report is about one.</param>
    /// <param name="remaining">How many older messages the folder still holds on the server.</param>
    public void RaiseBackfillProgressed(Guid accountId, int downloaded, bool finished, string? folderName = null, int remaining = 0)
    {
        var handlers = BackfillProgressed;
        if (handlers is null)
        {
            return;
        }

        var args = new BackfillProgressEventArgs(accountId, downloaded, finished, folderName, remaining);
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<BackfillProgressEventArgs>>())
        {
            try
            {
                handler(this, args);
            }
#pragma warning disable CA1031 // Subscribers are arbitrary UI code; one failing must not break the backfill or the other subscribers.
            catch (Exception)
#pragma warning restore CA1031
            {
                // Intentionally ignored, as in RaiseAccountSynced.
            }
        }
    }

    /// <summary>
    /// Raises <see cref="AccountSynced"/>; a failing subscriber never affects the sync or the other subscribers.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="newMessages">The number of newly cached messages.</param>
    public void RaiseAccountSynced(Guid accountId, int newMessages)
    {
        var handlers = AccountSynced;
        if (handlers is null)
        {
            return;
        }

        var args = new AccountSyncedEventArgs(accountId, newMessages);
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<AccountSyncedEventArgs>>())
        {
            try
            {
                handler(this, args);
            }
#pragma warning disable CA1031 // Subscribers are arbitrary UI code; one failing must not break the sync or the other subscribers.
            catch (Exception)
#pragma warning restore CA1031
            {
                // Intentionally ignored: no logger is available in this layer-neutral type, and a subscriber bug must not fail a completed sync.
            }
        }
    }
}
