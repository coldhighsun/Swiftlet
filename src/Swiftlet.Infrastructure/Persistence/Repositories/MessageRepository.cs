using Microsoft.EntityFrameworkCore;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Labels;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF Core implementation of <see cref="IMessageRepository"/> with FTS5 search. Mail is read from the account's own
/// database; labels are read from the global store and attached to the loaded messages.
/// </summary>
/// <param name="global">The global store holding accounts, labels and label links.</param>
/// <param name="contexts">The scope's account database contexts.</param>
internal sealed class MessageRepository(GlobalDbContext global, AccountContextProvider contexts) : IMessageRepository
{
    /// <summary>
    /// The minimum term length supported by the FTS5 trigram tokenizer.
    /// </summary>
    private const int MinTrigramLength = 3;

    /// <summary>
    /// The maximum number of identifiers per bulk statement.
    /// </summary>
    private const int BatchSize = 500;

    /// <inheritdoc />
    public async Task AddAsync(MailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var context = await contexts.GetAsync(message.AccountId, cancellationToken);
        context.Messages.Add(message);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<MailMessage?> GetAsync(Guid accountId, Guid id, CancellationToken cancellationToken = default)
    {
        var context = await contexts.GetAsync(accountId, cancellationToken);
        var message = await context.Messages
            .Include(m => m.Attachments)
            .FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
        if (message is not null)
        {
            await AttachLabelsAsync([message], cancellationToken);
        }

        return message;
    }

    /// <inheritdoc />
    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        await contexts.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task SaveLabelsAsync(MailMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var wanted = message.Labels.Select(l => l.Id).ToHashSet();
        var existing = await global.MessageLabels
            .Where(l => l.MessageId == message.Id)
            .ToListAsync(cancellationToken);

        global.MessageLabels.RemoveRange(existing.Where(l => !wanted.Contains(l.LabelId)));
        var present = existing.Select(l => l.LabelId).ToHashSet();
        foreach (var labelId in wanted.Where(id => !present.Contains(id)))
        {
            global.MessageLabels.Add(new MessageLabel { MessageId = message.Id, LabelId = labelId, AccountId = message.AccountId });
        }

        await global.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetPriorityAsync(
        Guid accountId,
        Guid messageId,
        MessagePriority? priority,
        CancellationToken cancellationToken = default)
    {
        var context = await contexts.GetAsync(accountId, cancellationToken);
        await context.Messages
            .Where(m => m.Id == messageId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.AiPriority, priority), cancellationToken);

        // A message this context already tracks would otherwise keep showing the old value to later reads.
        if (context.Messages.Local.FirstOrDefault(m => m.Id == messageId) is { } tracked)
        {
            await context.Entry(tracked).ReloadAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task AddRangeAsync(IReadOnlyCollection<MailMessage> messages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        foreach (var group in messages.GroupBy(m => m.AccountId))
        {
            var context = await contexts.GetAsync(group.Key, cancellationToken);
            context.Messages.AddRange(group);
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<uint> GetMaxUidAsync(Guid accountId, Guid folderId, CancellationToken cancellationToken = default)
    {
        var context = await contexts.GetAsync(accountId, cancellationToken);
        var max = await context.Messages
            .Where(m => m.FolderId == folderId)
            .MaxAsync(m => (uint?)m.Uid, cancellationToken);
        return max ?? 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, MailMessage>> FindByMessageIdsAsync(
        Guid accountId,
        IReadOnlyCollection<string> messageIds,
        Guid excludedFolderId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        var found = new Dictionary<string, MailMessage>(StringComparer.Ordinal);
        if (messageIds.Count == 0)
        {
            return found;
        }

        // Untracked: the results only serve as templates for copies, and nothing may stay in the change tracker.
        var context = await contexts.GetAsync(accountId, cancellationToken);
        foreach (var batch in messageIds.Distinct(StringComparer.Ordinal).Chunk(BatchSize))
        {
            var matches = await context.Messages
                .AsNoTracking()
                .Include(m => m.Attachments)
                .Where(m => m.FolderId != excludedFolderId && m.MessageIdHeader != null && batch.Contains(m.MessageIdHeader))
                .ToListAsync(cancellationToken);
            foreach (var match in matches)
            {
                found.TryAdd(match.MessageIdHeader!, match);
            }
        }

        return found;
    }

    /// <inheritdoc />
    public async Task<uint> GetMinUidAsync(Guid accountId, Guid folderId, CancellationToken cancellationToken = default)
    {
        var context = await contexts.GetAsync(accountId, cancellationToken);
        var min = await context.Messages
            .Where(m => m.FolderId == folderId)
            .MinAsync(m => (uint?)m.Uid, cancellationToken);
        return min ?? 0;
    }

    /// <inheritdoc />
    public async Task DeleteByFolderAsync(Guid accountId, Guid folderId, CancellationToken cancellationToken = default)
    {
        var context = await contexts.GetAsync(accountId, cancellationToken);
        var ids = await context.Messages.Where(m => m.FolderId == folderId).Select(m => m.Id).ToListAsync(cancellationToken);
        await context.Messages.Where(m => m.FolderId == folderId).ExecuteDeleteAsync(cancellationToken);
        await DeleteLabelLinksAsync(ids, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(Guid accountId, Guid folderId, uint uid, CancellationToken cancellationToken = default)
    {
        var context = await contexts.GetAsync(accountId, cancellationToken);
        return await context.Messages.AnyAsync(m => m.FolderId == folderId && m.Uid == uid, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MailMessage>> GetByFolderAsync(
        Guid accountId,
        Guid folderId,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        var context = await contexts.GetAsync(accountId, cancellationToken);
        var messages = await context.Messages
            .Include(m => m.Attachments)
            .Where(m => m.FolderId == folderId && !m.IsDeletePending)
            .OrderByDescending(m => m.SentAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
        await AttachLabelsAsync(messages, cancellationToken);
        return messages;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, int>> CountUnreadByFolderAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var context = await contexts.GetAsync(accountId, cancellationToken);
        return await UnreadCounts(context).ToDictionaryAsync(c => c.FolderId, c => c.Count, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<FolderMessageCount>> CountByFolderAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var context = await contexts.GetAsync(accountId, cancellationToken);

        // Only indexed columns are read (IX_Messages_Unread), so no message body is touched.
        return await context.Messages
            .Where(m => !m.IsDeletePending)
            .GroupBy(m => m.FolderId)
            .Select(g => new FolderMessageCount(g.Key, g.Count(), g.Count(m => !m.IsRead)))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Builds the query counting unread messages per folder; <c>IX_Messages_Unread</c> covers it, so no row is read.
    /// </summary>
    /// <param name="context">The account database.</param>
    /// <returns>The query.</returns>
    internal static IQueryable<UnreadFolderCount> UnreadCounts(AccountDbContext context)
    {
        return context.Messages
            .Where(m => !m.IsRead && !m.IsDeletePending)
            .GroupBy(m => m.FolderId)
            .Select(g => new UnreadFolderCount(g.Key, g.Count()));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MailMessage>> GetUnifiedInboxAsync(
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        // Every account database returns its newest skip+take inbox messages; the page is cut from the merged list.
        var candidates = new List<MailMessage>();
        foreach (var accountId in await GetAccountIdsAsync(cancellationToken))
        {
            var context = await contexts.GetAsync(accountId, cancellationToken);
            candidates.AddRange(await context.Messages
                .Include(m => m.Attachments)
                .Where(m => !m.IsDeletePending && context.Folders.Any(f => f.Id == m.FolderId && f.Role == FolderRole.Inbox))
                .OrderByDescending(m => m.SentAt)
                .Take(skip + take)
                .ToListAsync(cancellationToken));
        }

        var page = candidates.OrderByDescending(m => m.SentAt).Skip(skip).Take(take).ToList();
        await AttachLabelsAsync(page, cancellationToken);
        return page;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MailMessage>> GetByLabelAsync(
        Guid labelId,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        var links = await global.MessageLabels
            .AsNoTracking()
            .Where(l => l.LabelId == labelId)
            .Select(l => new { l.AccountId, l.MessageId })
            .ToListAsync(cancellationToken);

        var candidates = new List<MailMessage>();
        foreach (var byAccount in links.GroupBy(l => l.AccountId))
        {
            var context = await contexts.GetAsync(byAccount.Key, cancellationToken);
            foreach (var batch in byAccount.Select(l => l.MessageId).Chunk(BatchSize))
            {
                candidates.AddRange(await context.Messages
                    .Include(m => m.Attachments)
                    .Where(m => !m.IsDeletePending && batch.Contains(m.Id))
                    .ToListAsync(cancellationToken));
            }
        }

        var page = candidates.OrderByDescending(m => m.SentAt).Skip(skip).Take(take).ToList();
        await AttachLabelsAsync(page, cancellationToken);
        return page;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid accountId, Guid messageId, CancellationToken cancellationToken = default)
    {
        // Bulk delete relies on the database cascade (attachments) and the FTS delete trigger.
        var context = await contexts.GetAsync(accountId, cancellationToken);
        await context.Messages.Where(m => m.Id == messageId).ExecuteDeleteAsync(cancellationToken);
        await DeleteLabelLinksAsync([messageId], cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MessageFlagState>> GetFlagStatesAsync(
        Guid accountId,
        Guid folderId,
        CancellationToken cancellationToken = default)
    {
        // Projected and untracked: bodies are never read and nothing stays in the change tracker.
        var context = await contexts.GetAsync(accountId, cancellationToken);
        return await context.Messages
            .AsNoTracking()
            .Where(m => m.FolderId == folderId)
            .OrderBy(m => m.Uid)
            .Select(m => new MessageFlagState(m.Id, m.Uid, m.IsRead, m.IsFlagged, m.IsFlagSyncPending))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MessageFlagState>> GetPendingFlagStatesAsync(
        Guid accountId,
        Guid folderId,
        CancellationToken cancellationToken = default)
    {
        var context = await contexts.GetAsync(accountId, cancellationToken);
        return await context.Messages
            .AsNoTracking()
            .Where(m => m.FolderId == folderId && m.IsFlagSyncPending)
            .OrderBy(m => m.Uid)
            .Select(m => new MessageFlagState(m.Id, m.Uid, m.IsRead, m.IsFlagged, m.IsFlagSyncPending))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> ApplyServerFlagsAsync(
        Guid accountId,
        Guid messageId,
        bool isRead,
        bool isFlagged,
        CancellationToken cancellationToken = default)
    {
        // The pending check is part of the statement, so a user change made after the caller loaded the row still wins.
        var context = await contexts.GetAsync(accountId, cancellationToken);
        return await context.Messages
            .Where(m => m.Id == messageId && !m.IsFlagSyncPending)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(m => m.IsRead, isRead).SetProperty(m => m.IsFlagged, isFlagged),
                cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> ClearFlagSyncPendingAsync(
        Guid accountId,
        IReadOnlyCollection<MessageFlagState> pushed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pushed);
        if (pushed.Count == 0)
        {
            return 0;
        }

        // One UPDATE per row because every row has its own compare values; a single transaction keeps this to one
        // commit (one fsync) instead of one per row. A row the user changed after it was loaded no longer matches
        // and keeps its marker, so the next sync pushes the newer value.
        var context = await contexts.GetAsync(accountId, cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var cleared = 0;
        foreach (var state in pushed)
        {
            var id = state.Id;
            var isRead = state.IsRead;
            var isFlagged = state.IsFlagged;
            cleared += await context.Messages
                .Where(m => m.Id == id && m.IsRead == isRead && m.IsFlagged == isFlagged)
                .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.IsFlagSyncPending, false), cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return cleared;
    }

    /// <inheritdoc />
    public async Task DeleteRangeAsync(
        Guid accountId,
        IReadOnlyCollection<Guid> messageIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        if (messageIds.Count == 0)
        {
            return;
        }

        // Batches stay below SQLite's bound-parameter limit; cascades and the FTS trigger do the rest.
        var context = await contexts.GetAsync(accountId, cancellationToken);
        foreach (var batch in messageIds.Chunk(BatchSize))
        {
            await context.Messages.Where(m => batch.Contains(m.Id)).ExecuteDeleteAsync(cancellationToken);
        }

        await DeleteLabelLinksAsync(messageIds, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MailMessage>> SearchAsync(
        MessageSearchCriteria criteria,
        int skip,
        int take,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criteria);
        if (criteria.IsEmpty)
        {
            return [];
        }

        var accountIds = criteria.AccountId is { } single ? [single] : await GetAccountIdsAsync(cancellationToken);
        var results = new List<MailMessage>();
        foreach (var id in accountIds)
        {
            var context = await contexts.GetAsync(id, cancellationToken);
            // Every account returns its newest skip+take matches; the page is cut from the merged list.
            results.AddRange(await SearchAccountAsync(context, criteria, skip + take, cancellationToken));
        }

        var page = results.OrderByDescending(m => m.SentAt).Skip(skip).Take(take).ToList();
        await AttachLabelsAsync(page, cancellationToken);
        return page;
    }

    /// <inheritdoc />
    public async Task<int> MarkDeletePendingAsync(
        Guid accountId,
        IReadOnlyCollection<Guid> messageIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        var context = await contexts.GetAsync(accountId, cancellationToken);

        // One transaction, so a selection is hidden as a whole or not at all.
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var marked = 0;
        foreach (var batch in messageIds.Chunk(BatchSize))
        {
            marked += await context.Messages
                .Where(m => batch.Contains(m.Id))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(m => m.IsDeletePending, true)
                        .SetProperty(m => m.PendingMoveFolderId, (Guid?)null),
                    cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return marked;
    }

    /// <inheritdoc />
    public async Task<int> MarkMovePendingAsync(
        Guid accountId,
        IReadOnlyCollection<Guid> messageIds,
        Guid targetFolderId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messageIds);
        var context = await contexts.GetAsync(accountId, cancellationToken);

        // One transaction, so a selection is hidden as a whole or not at all.
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var marked = 0;
        foreach (var batch in messageIds.Chunk(BatchSize))
        {
            // A row that is already pending is left alone, so a late move never replaces a deletion.
            marked += await context.Messages
                .Where(m => batch.Contains(m.Id) && m.FolderId != targetFolderId && !m.IsDeletePending)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(m => m.IsDeletePending, true)
                        .SetProperty(m => m.PendingMoveFolderId, (Guid?)targetFolderId),
                    cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return marked;
    }

    /// <inheritdoc />
    public async Task<int> MarkFolderReadAsync(
        Guid accountId,
        Guid folderId,
        CancellationToken cancellationToken = default)
    {
        var context = await contexts.GetAsync(accountId, cancellationToken);
        return await context.Messages
            .Where(m => m.FolderId == folderId && !m.IsRead && !m.IsDeletePending)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(m => m.IsRead, true)
                    .SetProperty(m => m.IsFlagSyncPending, true),
                cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> MarkFolderDeletePendingAsync(
        Guid accountId,
        Guid folderId,
        uint throughUid,
        CancellationToken cancellationToken = default)
    {
        var context = await contexts.GetAsync(accountId, cancellationToken);
        return await context.Messages
            .Where(m => m.FolderId == folderId && m.Uid <= throughUid)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(m => m.IsDeletePending, true)
                    .SetProperty(m => m.PendingMoveFolderId, (Guid?)null),
                cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PendingDeletion>> GetPendingDeletionsAsync(
        Guid accountId,
        int take,
        CancellationToken cancellationToken = default)
    {
        var context = await contexts.GetAsync(accountId, cancellationToken);
        return await context.Messages
            .AsNoTracking()
            .Where(m => m.IsDeletePending)
            .OrderBy(m => m.FolderId)
            .ThenBy(m => m.PendingMoveFolderId)
            .ThenBy(m => m.Uid)
            .Take(take)
            .Select(m => new PendingDeletion(m.Id, m.FolderId, m.Uid, m.PendingMoveFolderId))
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> CountPendingDeletionsAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var context = await contexts.GetAsync(accountId, cancellationToken);
        return await context.Messages.CountAsync(m => m.IsDeletePending, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<uint>> GetPendingUidsAsync(
        Guid accountId,
        Guid folderId,
        CancellationToken cancellationToken = default)
    {
        var context = await contexts.GetAsync(accountId, cancellationToken);
        return await context.Messages
            .Where(m => m.FolderId == folderId && m.IsDeletePending)
            .Select(m => m.Uid)
            .ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PendingDeletion>> GetByUidsAsync(
        Guid accountId,
        Guid folderId,
        IReadOnlyCollection<uint> uids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uids);
        var context = await contexts.GetAsync(accountId, cancellationToken);
        var found = new List<PendingDeletion>();
        foreach (var batch in uids.Chunk(BatchSize))
        {
            found.AddRange(await context.Messages
                .AsNoTracking()
                .Where(m => m.FolderId == folderId && batch.Contains(m.Uid))
                .Select(m => new PendingDeletion(m.Id, m.FolderId, m.Uid))
                .ToListAsync(cancellationToken));
        }

        return found;
    }

    /// <inheritdoc />
    public async Task DeleteThroughUidAsync(
        Guid accountId,
        Guid folderId,
        uint throughUid,
        CancellationToken cancellationToken = default)
    {
        var context = await contexts.GetAsync(accountId, cancellationToken);
        var ids = await context.Messages
            .Where(m => m.FolderId == folderId && m.Uid <= throughUid)
            .Select(m => m.Id)
            .ToListAsync(cancellationToken);
        await DeleteRangeAsync(accountId, ids, cancellationToken);
    }

    /// <inheritdoc />
    public async Task ClearDeletePendingAsync(
        Guid accountId,
        Guid folderId,
        Guid? targetFolderId = null,
        CancellationToken cancellationToken = default)
    {
        var context = await contexts.GetAsync(accountId, cancellationToken);
        var pending = context.Messages.Where(m => m.FolderId == folderId && m.IsDeletePending);
        if (targetFolderId is not null)
        {
            pending = pending.Where(m => m.PendingMoveFolderId == targetFolderId);
        }

        await pending.ExecuteUpdateAsync(
            setters => setters
                .SetProperty(m => m.IsDeletePending, false)
                .SetProperty(m => m.PendingMoveFolderId, (Guid?)null),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task MoveToFolderAsync(
        Guid accountId,
        Guid folderId,
        IReadOnlyCollection<(Guid MessageId, uint Uid)> moves,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(moves);
        if (moves.Count == 0)
        {
            return;
        }

        var context = await contexts.GetAsync(accountId, cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        foreach (var (messageId, uid) in moves)
        {
            await context.Messages
                .Where(m => m.Id == messageId)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(m => m.FolderId, folderId)
                        .SetProperty(m => m.Uid, uid)
                        .SetProperty(m => m.IsDeletePending, false)
                        .SetProperty(m => m.PendingMoveFolderId, (Guid?)null),
                    cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SentMessageRecipients>> GetSentFolderRecipientsAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<SentMessageRecipients>();
        foreach (var accountId in await GetAccountIdsAsync(cancellationToken))
        {
            var context = await contexts.GetAsync(accountId, cancellationToken);
            result.AddRange(await context.Messages
                .AsNoTracking()
                .Where(m => context.Folders.Any(f => f.Id == m.FolderId && f.Role == FolderRole.Sent))
                .Select(m => new SentMessageRecipients(m.Recipients, m.SentAt))
                .ToListAsync(cancellationToken));
        }

        return result;
    }

    /// <summary>
    /// Searches one account's database.
    /// </summary>
    /// <param name="context">The account's context.</param>
    /// <param name="criteria">The search conditions; the account is the context's.</param>
    /// <param name="take">The maximum number of results.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The matching messages, newest first.</returns>
    private static async Task<List<MailMessage>> SearchAccountAsync(
        AccountDbContext context,
        MessageSearchCriteria criteria,
        int take,
        CancellationToken cancellationToken)
    {
        var textTerms = SplitTerms(criteria.Text);
        var fromTerms = SplitTerms(criteria.From);
        var subjectTerms = SplitTerms(criteria.Subject);

        // Every term long enough for the trigram index goes into one MATCH expression, limited to its columns.
        var match = string.Join(
            " AND ",
            textTerms.Where(IsIndexable).Select(Quote)
                .Concat(fromTerms.Where(IsIndexable).Select(t => $"{{FromName FromAddress}} : {Quote(t)}"))
                .Concat(subjectTerms.Where(IsIndexable).Select(t => $"Subject : {Quote(t)}")));
        var source = match.Length == 0
            ? context.Messages
            : context.Messages.FromSql(
                $"""
                SELECT m.* FROM Messages AS m
                JOIN MessageSearch ON MessageSearch.rowid = m.rowid
                WHERE MessageSearch MATCH {match}
                """);

        // Terms shorter than a trigram cannot use the FTS index; fall back to LIKE.
        foreach (var pattern in textTerms.Where(t => !IsIndexable(t)).Select(ToLikePattern))
        {
            source = source.Where(m =>
                EF.Functions.Like(m.Subject, pattern, "\\")
                || EF.Functions.Like(m.FromName, pattern, "\\")
                || EF.Functions.Like(m.FromAddress, pattern, "\\")
                || EF.Functions.Like(m.BodyText, pattern, "\\"));
        }

        foreach (var pattern in fromTerms.Where(t => !IsIndexable(t)).Select(ToLikePattern))
        {
            source = source.Where(m =>
                EF.Functions.Like(m.FromName, pattern, "\\") || EF.Functions.Like(m.FromAddress, pattern, "\\"));
        }

        foreach (var pattern in subjectTerms.Where(t => !IsIndexable(t)).Select(ToLikePattern))
        {
            source = source.Where(m => EF.Functions.Like(m.Subject, pattern, "\\"));
        }

        // Recipients are not part of the FTS index.
        foreach (var pattern in SplitTerms(criteria.To).Select(ToLikePattern))
        {
            source = source.Where(m => EF.Functions.Like(m.Recipients, pattern, "\\"));
        }

        if (criteria.SentFrom is { } sentFrom)
        {
            source = source.Where(m => m.SentAt >= sentFrom);
        }

        if (criteria.SentBefore is { } sentBefore)
        {
            source = source.Where(m => m.SentAt < sentBefore);
        }

        if (criteria.HasAttachments)
        {
            source = source.Where(m => m.Attachments.Any());
        }

        if (criteria.UnreadOnly)
        {
            source = source.Where(m => !m.IsRead);
        }

        if (criteria.FlaggedOnly)
        {
            source = source.Where(m => m.IsFlagged);
        }

        source = source.Where(m => !m.IsDeletePending);
        if (criteria.FolderId is { } folderId)
        {
            source = source.Where(m => m.FolderId == folderId);
        }
        else
        {
            // The "All Mail" copy of a message that also lives in another folder would show up twice; keep only the other
            // copy. Messages without a Message-ID cannot be matched and stay visible.
            source = source.Where(m =>
                m.MessageIdHeader == null
                || !context.Folders.Any(f => f.Id == m.FolderId && f.Role == FolderRole.AllMail)
                || !context.Messages.Any(n => !n.IsDeletePending && n.FolderId != m.FolderId && n.MessageIdHeader == m.MessageIdHeader));
        }

        return await source
            .Include(m => m.Attachments)
            .OrderByDescending(m => m.SentAt)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Gets the identifiers of all accounts.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The account identifiers.</returns>
    private async Task<List<Guid>> GetAccountIdsAsync(CancellationToken cancellationToken)
    {
        return await global.Accounts.AsNoTracking().Select(a => a.Id).ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Loads the labels of the given messages from the global store and attaches them.
    /// </summary>
    /// <param name="messages">The loaded messages.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the labels are attached.</returns>
    private async Task AttachLabelsAsync(List<MailMessage> messages, CancellationToken cancellationToken)
    {
        if (messages.Count == 0)
        {
            return;
        }

        var labelsByMessage = new Dictionary<Guid, List<Label>>();
        foreach (var batch in messages.Select(m => m.Id).Chunk(BatchSize))
        {
            var links = await global.MessageLabels
                .AsNoTracking()
                .Where(l => batch.Contains(l.MessageId))
                .Join(global.Labels, l => l.LabelId, label => label.Id, (l, label) => new { l.MessageId, Label = label })
                .ToListAsync(cancellationToken);
            foreach (var link in links)
            {
                if (!labelsByMessage.TryGetValue(link.MessageId, out var list))
                {
                    list = [];
                    labelsByMessage[link.MessageId] = list;
                }

                list.Add(link.Label);
            }
        }

        foreach (var message in messages)
        {
            message.SetLabels(labelsByMessage.TryGetValue(message.Id, out var labels) ? labels : []);
        }
    }

    /// <summary>
    /// Removes the label links of deleted messages. A failure here only leaves harmless orphan links, because label
    /// queries always go through existing messages.
    /// </summary>
    /// <param name="messageIds">The deleted message identifiers.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the links are removed.</returns>
    private async Task DeleteLabelLinksAsync(IReadOnlyCollection<Guid> messageIds, CancellationToken cancellationToken)
    {
        foreach (var batch in messageIds.Chunk(BatchSize))
        {
            await global.MessageLabels.Where(l => batch.Contains(l.MessageId)).ExecuteDeleteAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Splits entered search text into its terms.
    /// </summary>
    /// <param name="text">The entered text.</param>
    /// <returns>The space-separated terms.</returns>
    private static string[] SplitTerms(string text)
    {
        return text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// Says whether a term can be looked up in the trigram index.
    /// </summary>
    /// <param name="term">The term.</param>
    /// <returns><see langword="true"/> when the term is long enough.</returns>
    private static bool IsIndexable(string term)
    {
        return term.Length >= MinTrigramLength;
    }

    /// <summary>
    /// Escapes a term into an FTS5 string literal, so user input is never read as query syntax.
    /// </summary>
    /// <param name="term">The raw term.</param>
    /// <returns>The quoted term.</returns>
    private static string Quote(string term)
    {
        return $"\"{term.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    /// <summary>
    /// Builds the LIKE pattern matching values that contain a term.
    /// </summary>
    /// <param name="term">The raw term.</param>
    /// <returns>The pattern, using backslash as the escape character.</returns>
    private static string ToLikePattern(string term)
    {
        return $"%{EscapeLike(term)}%";
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
