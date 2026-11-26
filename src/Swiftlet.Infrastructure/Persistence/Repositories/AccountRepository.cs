using Microsoft.EntityFrameworkCore;
using Swiftlet.Domain.Accounts;

namespace Swiftlet.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IAccountRepository"/>.
/// </summary>
/// <param name="context">The global database context.</param>
/// <param name="accountDatabases">The factory of the per-account mail databases.</param>
internal sealed class AccountRepository(GlobalDbContext context, IAccountDbContextFactory accountDatabases) : IAccountRepository
{
    /// <inheritdoc />
    public async Task AddAsync(MailAccount account, CancellationToken cancellationToken = default)
    {
        context.Accounts.Add(account);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<MailAccount?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await context.Accounts.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MailAccount>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await context.Accounts.OrderBy(a => a.CreatedAt).ToListAsync(cancellationToken);
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
