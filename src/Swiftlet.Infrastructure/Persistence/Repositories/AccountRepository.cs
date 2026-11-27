using Microsoft.EntityFrameworkCore;
using Swiftlet.Application.Logging;
using Swiftlet.Domain.Accounts;

namespace Swiftlet.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IAccountRepository"/>.
/// </summary>
/// <param name="context">The global database context.</param>
/// <param name="accountDatabases">The factory of the per-account mail databases.</param>
/// <param name="names">Remembers the names shown in the log; optional.</param>
internal sealed class AccountRepository(GlobalDbContext context, IAccountDbContextFactory accountDatabases, LogNames? names = null)
    : IAccountRepository
{
    /// <inheritdoc />
    public async Task AddAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        // A new account goes to the end of the tree.
        var last = await context.Accounts.MaxAsync(a => (int?)a.SortOrder, cancellationToken);
        account.MoveTo(last is { } value ? value + 1 : 0);
        context.Accounts.Add(account);
        await context.SaveChangesAsync(cancellationToken);
        names?.RememberAccount(account.Id, account.EmailAddress);
    }

    /// <inheritdoc />
    public async Task<MailAccount?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var account = await context.Accounts.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
        if (account is not null)
        {
            names?.RememberAccount(account.Id, account.EmailAddress);
        }

        return account;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MailAccount>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var accounts = await context.Accounts.OrderBy(a => a.SortOrder).ThenBy(a => a.CreatedAt).ToListAsync(cancellationToken);
        foreach (var account in accounts)
        {
            names?.RememberAccount(account.Id, account.EmailAddress);
        }

        return accounts;
    }

    /// <inheritdoc />
    public async Task UpdateAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);

        // The account is tracked by this context (it was read from it), so only the changed columns are written.
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task ReorderAsync(IReadOnlyList<Guid> orderedIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);

        // One transaction so a failure part-way never leaves a half-numbered list. Column-only updates keep tracked
        // account entities of this context untouched.
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        for (var i = 0; i < orderedIds.Count; i++)
        {
            var id = orderedIds[i];
            var order = i;
            await context.Accounts.Where(a => a.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.SortOrder, order), cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetExpandedAsync(Guid id, bool expanded, CancellationToken cancellationToken = default)
    {
        await context.Accounts.Where(a => a.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.IsExpanded, expanded), cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // The account row is the point of no return. Label links of its messages are removed with it (the messages
        // themselves live in the account's own database), and the database files go last.
        await context.MessageLabels.Where(l => l.AccountId == id).ExecuteDeleteAsync(cancellationToken);
        await context.Accounts.Where(a => a.Id == id).ExecuteDeleteAsync(cancellationToken);
        await accountDatabases.DeleteAsync(id, cancellationToken);
    }
}
