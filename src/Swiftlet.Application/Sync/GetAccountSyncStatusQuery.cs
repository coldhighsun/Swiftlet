using Mediator;

namespace Swiftlet.Application.Sync;

/// <summary>
/// Gets how the synchronization of an account has gone since the application started.
/// </summary>
/// <param name="AccountId">The account.</param>
public sealed record GetAccountSyncStatusQuery(Guid AccountId) : IQuery<AccountSyncStatus>;

/// <summary>
/// Handles <see cref="GetAccountSyncStatusQuery"/>.
/// </summary>
/// <param name="statuses">The recorded statuses.</param>
public sealed class GetAccountSyncStatusQueryHandler(AccountSyncStatusTracker statuses)
    : IQueryHandler<GetAccountSyncStatusQuery, AccountSyncStatus>
{
    /// <inheritdoc />
    public ValueTask<AccountSyncStatus> Handle(GetAccountSyncStatusQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return ValueTask.FromResult(statuses.Get(query.AccountId));
    }
}
