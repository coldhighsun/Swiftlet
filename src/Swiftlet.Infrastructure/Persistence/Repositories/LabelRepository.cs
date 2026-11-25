using Microsoft.EntityFrameworkCore;
using Swiftlet.Domain.Labels;

namespace Swiftlet.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of <see cref="ILabelRepository"/>.
/// </summary>
/// <param name="context">The database context.</param>
internal sealed class LabelRepository(SwiftletDbContext context) : ILabelRepository
{
    /// <inheritdoc />
    public async Task AddAsync(Label label, CancellationToken cancellationToken = default)
    {
        context.Labels.Add(label);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Label?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await context.Labels.FirstOrDefaultAsync(l => l.Id == id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Label>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var labels = await context.Labels.ToListAsync(cancellationToken);

        // Sorted in memory because SQLite's default collation is case-sensitive and ASCII-only.
        return labels.OrderBy(l => l.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <inheritdoc />
    public async Task<bool> NameExistsAsync(string name, Guid? exceptId, CancellationToken cancellationToken = default)
    {
        var trimmed = (name ?? string.Empty).Trim();
        var candidates = await context.Labels
            .Where(l => exceptId == null || l.Id != exceptId)
            .Select(l => l.Name)
            .ToListAsync(cancellationToken);

        // Compared in memory because SQLite's lower() and NOCASE only fold ASCII.
        return candidates.Exists(existing => string.Equals(existing.Trim(), trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        // Bulk delete relies on the join table's cascade; messages are untouched.
        await context.Labels.Where(l => l.Id == id).ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await context.SaveChangesAsync(cancellationToken);
    }
}
