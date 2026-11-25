using Microsoft.EntityFrameworkCore;
using Swiftlet.Domain.Folders;

namespace Swiftlet.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IFolderRepository"/>.
/// </summary>
/// <param name="context">The database context.</param>
internal sealed class FolderRepository(SwiftletDbContext context) : IFolderRepository
{
    /// <inheritdoc />
    public async Task AddAsync(MailFolder folder, CancellationToken cancellationToken = default)
    {
        context.Folders.Add(folder);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<MailFolder?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await context.Folders.FirstOrDefaultAsync(f => f.Id == id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MailFolder>> GetByAccountAsync(
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        return await context.Folders
            .Where(f => f.AccountId == accountId)
            .OrderBy(f => f.FullName)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await context.SaveChangesAsync(cancellationToken);
    }
}
