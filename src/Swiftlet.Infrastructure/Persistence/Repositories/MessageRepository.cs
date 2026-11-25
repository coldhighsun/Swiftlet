using Microsoft.EntityFrameworkCore;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IMessageRepository"/> with FTS5 search.
/// </summary>
/// <param name="context">The database context.</param>
internal sealed class MessageRepository(SwiftletDbContext context) : IMessageRepository
{
    /// <summary>
    /// The minimum term length supported by the FTS5 trigram tokenizer.
    /// </summary>
    private const int MinTrigramLength = 3;

    /// <inheritdoc />
    public async Task AddAsync(MailMessage message, CancellationToken cancellationToken = default)
    {
        context.Messages.Add(message);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<MailMessage?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await context.Messages
            .Include(m => m.Attachments)
            .Include(m => m.Labels)
            .FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task AddRangeAsync(IReadOnlyCollection<MailMessage> messages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        context.Messages.AddRange(messages);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<uint> GetMaxUidAsync(Guid folderId, CancellationToken cancellationToken = default)
    {
        var max = await context.Messages
            .Where(m => m.FolderId == folderId)
            .MaxAsync(m => (uint?)m.Uid, cancellationToken);
        return max ?? 0;
    }

    /// <inheritdoc />
    public async Task DeleteByFolderAsync(Guid folderId, CancellationToken cancellationToken = default)
    {
        await context.Messages.Where(m => m.FolderId == folderId).ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(Guid folderId, uint uid, CancellationToken cancellationToken = default)
    {
        return await context.Messages.AnyAsync(m => m.FolderId == folderId && m.Uid == uid, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MailMessage>> GetByFolderAsync(
        Guid folderId,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        return await context.Messages
            .Include(m => m.Attachments)
            .Include(m => m.Labels)
            .Where(m => m.FolderId == folderId)
            .OrderByDescending(m => m.SentAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MailMessage>> GetUnifiedInboxAsync(
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        return await context.Messages
            .Include(m => m.Attachments)
            .Include(m => m.Labels)
            .Where(m => context.Folders.Any(f => f.Id == m.FolderId && f.Role == FolderRole.Inbox))
            .OrderByDescending(m => m.SentAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MailMessage>> GetByLabelAsync(
        Guid labelId,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        return await context.Messages
            .Include(m => m.Attachments)
            .Include(m => m.Labels)
            .Where(m => m.Labels.Any(l => l.Id == labelId))
            .OrderByDescending(m => m.SentAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        // Bulk delete relies on the database cascades (attachments, label links) and the FTS delete trigger.
        await context.Messages.Where(m => m.Id == messageId).ExecuteDeleteAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MailMessage>> SearchAsync(
        string query,
        Guid? accountId,
        int take,
        CancellationToken cancellationToken = default)
    {
        var terms = (query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
        {
            return [];
        }

        IQueryable<MailMessage> source;
        if (terms.All(t => t.Length >= MinTrigramLength))
        {
            var match = string.Join(' ', terms.Select(t => $"\"{t.Replace("\"", "\"\"", StringComparison.Ordinal)}\""));
            source = context.Messages.FromSql(
                $"""
                SELECT m.* FROM Messages AS m
                JOIN MessageSearch ON MessageSearch.rowid = m.rowid
                WHERE MessageSearch MATCH {match}
                """);
        }
        else
        {
            // Terms shorter than a trigram cannot use the FTS index; fall back to LIKE.
            source = context.Messages;
            foreach (var term in terms)
            {
                var pattern = $"%{EscapeLike(term)}%";
                source = source.Where(m =>
                    EF.Functions.Like(m.Subject, pattern, "\\")
                    || EF.Functions.Like(m.FromName, pattern, "\\")
                    || EF.Functions.Like(m.FromAddress, pattern, "\\")
                    || EF.Functions.Like(m.BodyText, pattern, "\\"));
            }
        }

        if (accountId is { } id)
        {
            source = source.Where(m => m.AccountId == id);
        }

        return await source
            .Include(m => m.Attachments)
            .Include(m => m.Labels)
            .OrderByDescending(m => m.SentAt)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Escapes LIKE wildcards using backslash as the escape character.
    /// </summary>
    /// <param name="term">The raw term.</param>
    /// <returns>The escaped term.</returns>
    private static string EscapeLike(string term)
    {
        return term
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }
}
