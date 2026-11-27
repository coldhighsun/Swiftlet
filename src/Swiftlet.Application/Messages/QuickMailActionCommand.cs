using Mediator;
using Swiftlet.Application.Logging;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Application.Messages;

/// <summary>
/// What can be done with a newly arrived message in one step.
/// </summary>
public enum QuickMailAction
{
    /// <summary>
    /// Only marks the message as read.
    /// </summary>
    MarkRead,

    /// <summary>
    /// Stars the message.
    /// </summary>
    Star,

    /// <summary>
    /// Moves the message to the archive folder.
    /// </summary>
    Archive,

    /// <summary>
    /// Moves the message to Trash.
    /// </summary>
    Delete,
}

/// <summary>
/// How a quick action ended.
/// </summary>
public enum QuickMailActionResult
{
    /// <summary>
    /// The action was applied.
    /// </summary>
    Done,

    /// <summary>
    /// The message is no longer where it was announced (removed, moved or about to be), so nothing was changed.
    /// </summary>
    Gone,

    /// <summary>
    /// The account cannot carry out this action, so nothing was changed.
    /// </summary>
    Unavailable,
}

/// <summary>
/// Which quick actions that move a message are possible.
/// </summary>
/// <param name="CanDelete">Whether the message can be moved to Trash. A permanent delete is never offered.</param>
/// <param name="CanArchive">Whether the message can be archived.</param>
public sealed record QuickActionAvailability(bool CanDelete, bool CanArchive);

/// <summary>
/// Applies one action to a message and marks it as read. The change is applied locally at once and written to the server
/// in the background.
/// </summary>
/// <param name="AccountId">The account that owns the message.</param>
/// <param name="MessageId">The message identifier.</param>
/// <param name="FolderId">The folder the message was in when it was offered to the user.</param>
/// <param name="Action">What to do.</param>
public sealed record QuickMailActionCommand(Guid AccountId, Guid MessageId, Guid FolderId, QuickMailAction Action)
    : ICommand<QuickMailActionResult>, IFolderRequest, ILogDetails
{
    /// <inheritdoc />
    public string DescribeForLog(LogNames names)
    {
        return $"{Action} message {MessageId}";
    }
}

/// <summary>
/// Handles <see cref="QuickMailActionCommand"/>.
/// </summary>
/// <param name="accounts">The account repository.</param>
/// <param name="folders">The folder repository.</param>
/// <param name="messages">The message repository.</param>
/// <param name="deletions">Wakes the background deletion and moving.</param>
/// <param name="flags">Wakes the background flag push; optional.</param>
public sealed class QuickMailActionCommandHandler(
    IAccountRepository accounts,
    IFolderRepository folders,
    IMessageRepository messages,
    DeletionSignal deletions,
    FlagPushSignal? flags = null)
    : ICommandHandler<QuickMailActionCommand, QuickMailActionResult>
{
    /// <inheritdoc />
    public async ValueTask<QuickMailActionResult> Handle(QuickMailActionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var message = await messages.GetAsync(command.AccountId, command.MessageId, cancellationToken);

        // A moved row keeps its identifier: deleting one that now sits in Trash would remove it for good.
        if (message is null || message.FolderId != command.FolderId || message.IsDeletePending)
        {
            return QuickMailActionResult.Gone;
        }

        var account = await accounts.GetAsync(command.AccountId, cancellationToken);
        if (account is null)
        {
            return QuickMailActionResult.Gone;
        }

        var cached = await folders.GetByAccountAsync(command.AccountId, cancellationToken);
        MailFolder? target = null;
        if (command.Action == QuickMailAction.Archive)
        {
            target = QuickActionRules.ArchiveTarget(account, cached, command.FolderId);
            if (target is null)
            {
                return QuickMailActionResult.Unavailable;
            }
        }
        else if (command.Action == QuickMailAction.Delete && !QuickActionRules.CanMoveToTrash(cached, command.FolderId))
        {
            return QuickMailActionResult.Unavailable;
        }

        message.SetRead(true);
        if (command.Action == QuickMailAction.Star)
        {
            message.SetFlagged(true);
        }

        await messages.SaveChangesAsync(cancellationToken);
        flags?.Notify(command.AccountId);

        if (command.Action is QuickMailAction.Archive or QuickMailAction.Delete)
        {
            if (target is not null)
            {
                await messages.MarkMovePendingAsync(command.AccountId, [command.MessageId], target.Id, cancellationToken);
            }
            else
            {
                await messages.MarkDeletePendingAsync(command.AccountId, [command.MessageId], cancellationToken);
            }

            deletions.Notify();
        }

        return QuickMailActionResult.Done;
    }
}

/// <summary>
/// Gets which quick actions are possible for messages of a folder.
/// </summary>
/// <param name="AccountId">The account that owns the folder.</param>
/// <param name="FolderId">The folder identifier.</param>
public sealed record GetQuickActionsQuery(Guid AccountId, Guid FolderId) : IQuery<QuickActionAvailability>, IFolderRequest;

/// <summary>
/// Handles <see cref="GetQuickActionsQuery"/>.
/// </summary>
/// <param name="accounts">The account repository.</param>
/// <param name="folders">The folder repository.</param>
public sealed class GetQuickActionsQueryHandler(IAccountRepository accounts, IFolderRepository folders)
    : IQueryHandler<GetQuickActionsQuery, QuickActionAvailability>
{
    /// <inheritdoc />
    public async ValueTask<QuickActionAvailability> Handle(GetQuickActionsQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var account = await accounts.GetAsync(query.AccountId, cancellationToken);
        if (account is null)
        {
            return new QuickActionAvailability(false, false);
        }

        var cached = await folders.GetByAccountAsync(query.AccountId, cancellationToken);
        return new QuickActionAvailability(
            QuickActionRules.CanMoveToTrash(cached, query.FolderId),
            QuickActionRules.ArchiveTarget(account, cached, query.FolderId) is not null);
    }
}

/// <summary>
/// The rules shared by the quick action command and the query that enables its buttons.
/// </summary>
internal static class QuickActionRules
{
    /// <summary>
    /// Determines whether deleting from a folder moves the message to Trash instead of removing it for good.
    /// </summary>
    /// <param name="folders">The cached folders of the account.</param>
    /// <param name="folderId">The folder holding the message.</param>
    /// <returns><see langword="true"/> if the delete can be undone.</returns>
    public static bool CanMoveToTrash(IReadOnlyCollection<MailFolder> folders, Guid folderId)
    {
        return folders.Any(f => f.Id == folderId && f.Role != FolderRole.Trash)
            && folders.Any(f => f.Role == FolderRole.Trash);
    }

    /// <summary>
    /// Gets the folder archiving moves a message of a folder to.
    /// </summary>
    /// <param name="account">The account.</param>
    /// <param name="folders">The cached folders of the account.</param>
    /// <param name="folderId">The folder holding the message.</param>
    /// <returns>The archive folder, or <see langword="null"/> if there is none or the message is already in it.</returns>
    public static MailFolder? ArchiveTarget(MailAccount account, IReadOnlyCollection<MailFolder> folders, Guid folderId)
    {
        var target = ArchiveFolderResolver.Resolve(account, folders);
        return target?.Id == folderId ? null : target;
    }
}
