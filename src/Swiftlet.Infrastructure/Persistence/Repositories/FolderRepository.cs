using Microsoft.EntityFrameworkCore;
using Swiftlet.Application.Logging;
using Swiftlet.Domain.Folders;

namespace Swiftlet.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IFolderRepository"/> on the per-account databases.
/// </summary>
/// <param name="contexts">The scope's account database contexts.</param>
/// <param name="names">Remembers the names shown in the log; optional.</param>
internal sealed class FolderRepository(AccountContextProvider contexts, LogNames? names = null) : IFolderRepository
{
    /// <inheritdoc />
    public async Task AddAsync(MailFolder folder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var context = await contexts.GetAsync(folder.AccountId, cancellationToken);
        context.Folders.Add(folder);
        await context.SaveChangesAsync(cancellationToken);
        names?.RememberFolder(folder.Id, folder.FullName);
    }

    /// <inheritdoc />
    public async Task<MailFolder?> GetAsync(Guid accountId, Guid id, CancellationToken cancellationToken = default)
    {
        var context = await contexts.GetAsync(accountId, cancellationToken);
        var folder = await context.Folders.FirstOrDefaultAsync(f => f.Id == id, cancellationToken);
        if (folder is not null)
        {
            names?.RememberFolder(folder.Id, folder.FullName);
        }

        return folder;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MailFolder>> GetByAccountAsync(
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        var context = await contexts.GetAsync(accountId, cancellationToken);
        var folders = await context.Folders
            .Where(f => f.AccountId == accountId)
            .OrderBy(f => f.FullName)
            .ToListAsync(cancellationToken);
        foreach (var folder in folders)
        {
            names?.RememberFolder(folder.Id, folder.FullName);
        }

        return folders;
    }

    /// <inheritdoc />
    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await contexts.SaveChangesAsync(cancellationToken);
    }
}
