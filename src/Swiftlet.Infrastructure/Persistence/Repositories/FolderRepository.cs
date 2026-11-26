using Microsoft.EntityFrameworkCore;
using Swiftlet.Domain.Folders;

namespace Swiftlet.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IFolderRepository"/> on the per-account databases.
/// </summary>
/// <param name="contexts">The scope's account database contexts.</param>
internal sealed class FolderRepository(AccountContextProvider contexts) : IFolderRepository
{
    /// <inheritdoc />
    public async Task AddAsync(MailFolder folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var context = await contexts.GetAsync(folder.AccountId, cancellationToken);
        context.Folders.Add(folder);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<MailFolder?> GetAsync(Guid accountId, Guid id, CancellationToken cancellationToken = default)
    {
        var context = await contexts.GetAsync(accountId, cancellationToken);
        return await context.Folders.FirstOrDefaultAsync(f => f.Id == id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MailFolder>> GetByAccountAsync(
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        var context = await contexts.GetAsync(accountId, cancellationToken);
        return await context.Folders
            .Where(f => f.AccountId == accountId)
            .OrderBy(f => f.FullName)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await contexts.SaveChangesAsync(cancellationToken);
    }
}
