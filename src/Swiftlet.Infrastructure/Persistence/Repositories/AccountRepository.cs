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
        var accounts = await context.Accounts.OrderBy(a => a.CreatedAt).ToListAsync(cancellationToken);
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
    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // The account row is the point of no return. Label links of its messages are removed with it (the messages
        // themselves live in the account's own database), and the database files go last.
        await context.MessageLabels.Where(l => l.AccountId == id).ExecuteDeleteAsync(cancellationToken);
        await context.Accounts.Where(a => a.Id == id).ExecuteDeleteAsync(cancellationToken);
        await accountDatabases.DeleteAsync(id, cancellationToken);
    }
}
