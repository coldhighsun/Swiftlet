using Mediator;
using Swiftlet.Application.Logging;
using Swiftlet.Domain.Accounts;

namespace Swiftlet.Application.Accounts;

/// <summary>
/// Stores the order of the accounts in the folder tree.
/// </summary>
/// <param name="AccountIds">The account identifiers in the wanted order.</param>
public sealed record ReorderAccountsCommand(IReadOnlyList<Guid> AccountIds) : ICommand<Unit>, ILogDetails
{
    /// <inheritdoc />
    public string DescribeForLog(LogNames names)
    {
        return $"count={AccountIds.Count}";
    }
}

/// <summary>
/// Handles <see cref="ReorderAccountsCommand"/>.
/// </summary>
/// <param name="accounts">The account repository.</param>
public sealed class ReorderAccountsCommandHandler(IAccountRepository accounts) : ICommandHandler<ReorderAccountsCommand, Unit>
{
    /// <inheritdoc />
    public async ValueTask<Unit> Handle(ReorderAccountsCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await accounts.ReorderAsync(command.AccountIds, cancellationToken);
        return Unit.Value;
    }
}

/// <summary>
/// Stores whether an account node is expanded in the folder tree.
/// </summary>
/// <param name="AccountId">The account identifier.</param>
/// <param name="IsExpanded">Whether the node is expanded.</param>
public sealed record SetAccountExpandedCommand(Guid AccountId, bool IsExpanded) : ICommand<Unit>, IAccountRequest, ILogDetails
{
    /// <inheritdoc />
    public string DescribeForLog(LogNames names)
    {
        return $"expanded={IsExpanded}";
    }
}

/// <summary>
/// Handles <see cref="SetAccountExpandedCommand"/>.
/// </summary>
/// <param name="accounts">The account repository.</param>
public sealed class SetAccountExpandedCommandHandler(IAccountRepository accounts)
    : ICommandHandler<SetAccountExpandedCommand, Unit>
{
    /// <inheritdoc />
    public async ValueTask<Unit> Handle(SetAccountExpandedCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        await accounts.SetExpandedAsync(command.AccountId, command.IsExpanded, cancellationToken);
        return Unit.Value;
    }
}
