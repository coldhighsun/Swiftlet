using Microsoft.Extensions.Logging;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Sync;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Application.Messages;

/// <summary>
/// Writes local read and star changes to the server without a full sync.
/// A change that cannot be written stays pending, and the next regular sync writes it.
/// </summary>
/// <param name="folders">The folder repository.</param>
/// <param name="messages">The message repository.</param>
/// <param name="flags">Serializes the flag work of a folder with the regular sync.</param>
/// <param name="logger">The logger.</param>
public sealed class PendingFlagService(
    IFolderRepository folders,
    IMessageRepository messages,
    FlagCoordinator flags,
    ILogger<PendingFlagService> logger)
{
    /// <summary>
    /// Gets the folders of an account that hold changes waiting to be written.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The folder identifiers.</returns>
    public async Task<IReadOnlyList<Guid>> GetPendingFoldersAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        var result = new List<Guid>();
        foreach (var folder in await folders.GetByAccountAsync(accountId, cancellationToken))
        {
            if ((await messages.GetPendingFlagStatesAsync(accountId, folder.Id, cancellationToken)).Count > 0)
            {
                result.Add(folder.Id);
            }
        }

        return result;
    }

    /// <summary>
    /// Writes the pending changes of one folder to the server.
    /// </summary>
    /// <param name="session">The IMAP session.</param>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="folderId">The folder identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of messages whose change is now on the server.</returns>
    public Task<int> PushFolderAsync(IImapSession session, Guid accountId, Guid folderId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        return flags.RunExclusiveAsync(
            folderId,
            async () =>
            {
                var folder = await folders.GetAsync(accountId, folderId, cancellationToken);
                if (folder is null)
                {
                    return 0;
                }

                var pending = await messages.GetPendingFlagStatesAsync(accountId, folderId, cancellationToken);
                if (pending.Count == 0)
                {
                    return 0;
                }

                // Cached UIDs may name other messages after a UIDVALIDITY change; the regular sync discards them.
                if (await session.GetUidValidityAsync(folder.FullName, cancellationToken) != folder.UidValidity)
                {
                    return 0;
                }

                var updates = pending.Select(m => new FlagUpdate(m.Uid, m.IsRead, m.IsFlagged)).ToList();
                await session.SetFlagsAsync(folder.FullName, updates, cancellationToken);

                // Only rows that still hold the pushed values are cleared; a change made meanwhile stays pending.
                var cleared = await messages.ClearFlagSyncPendingAsync(accountId, pending, cancellationToken);
                logger.LogInformation("Pushed flags of {Count} messages in {Folder}.", cleared, folder.FullName);
                return cleared;
            },
            cancellationToken);
    }
}
