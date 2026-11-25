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
/// Notifies subscribers about completed synchronizations.
/// </summary>
public interface ISyncEvents
{
    /// <summary>
    /// Raised after an account was synchronized. May be raised on a background thread.
    /// </summary>
    event EventHandler<AccountSyncedEventArgs>? AccountSynced;
}

/// <summary>
/// The default <see cref="ISyncEvents"/> implementation, which the sync handler raises events on.
/// </summary>
public sealed class SyncEvents : ISyncEvents
{
    /// <inheritdoc />
    public event EventHandler<AccountSyncedEventArgs>? AccountSynced;

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
