using Microsoft.EntityFrameworkCore;
using Swiftlet.Domain.Contacts;

namespace Swiftlet.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IContactRepository"/>.
/// </summary>
/// <param name="context">The global database context.</param>
internal sealed class ContactRepository(GlobalDbContext context) : IContactRepository
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<Contact>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var contacts = await context.Contacts.ToListAsync(cancellationToken);

        // Sorted in memory because SQLite's default collation is case-sensitive and ASCII-only.
        return contacts
            .OrderBy(c => c.DisplayName.Length > 0 ? c.DisplayName : c.Emails[0], StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<Contact?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await context.Contacts.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task AddRangeAsync(IReadOnlyCollection<Contact> contacts, CancellationToken cancellationToken = default)
    {
        context.Contacts.AddRange(contacts);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await context.Contacts.Where(c => c.Id == id).ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SentRecipient>> GetSentRecipientsAsync(CancellationToken cancellationToken = default)
    {
        return await context.SentRecipients.AsNoTracking().ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task RecordSentRecipientsAsync(
        IReadOnlyCollection<SentRecipient> recipients,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(recipients);

        // An upsert, so that two messages sent at the same time cannot collide on the key.
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        foreach (var recipient in recipients)
        {
            var lastUsed = recipient.LastUsedAt.ToUnixTimeMilliseconds();
            await context.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO SentRecipients (AccountId, Address, Name, LastUsedAt)
                VALUES ({recipient.AccountId}, {recipient.Address}, {recipient.Name}, {lastUsed})
                ON CONFLICT (AccountId, Address) DO UPDATE SET
                    Name = CASE WHEN excluded.Name = '' THEN Name ELSE excluded.Name END,
                    LastUsedAt = excluded.LastUsedAt;
                """,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetDismissedAddressesAsync(CancellationToken cancellationToken = default)
    {
        return await context.DismissedAddresses.AsNoTracking().Select(a => a.Address).ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task DismissAddressAsync(string address, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        var key = address.Trim().ToLowerInvariant();
        await context.Database.ExecuteSqlAsync(
            $"INSERT OR IGNORE INTO DismissedAddresses (Address) VALUES ({key});",
            cancellationToken);
    }
}
