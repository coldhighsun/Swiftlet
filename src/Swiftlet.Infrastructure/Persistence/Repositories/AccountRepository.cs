using Microsoft.EntityFrameworkCore;
using Swiftlet.Domain.Accounts;

namespace Swiftlet.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IAccountRepository"/>.
/// </summary>
/// <param name="context">The database context.</param>
internal sealed class AccountRepository(SwiftletDbContext context) : IAccountRepository
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
}
