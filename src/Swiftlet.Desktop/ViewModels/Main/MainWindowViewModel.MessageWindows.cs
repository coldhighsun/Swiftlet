using Microsoft.Extensions.Logging;
using Swiftlet.Application.Messages;
using Swiftlet.Desktop.ViewModels.Compose;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Desktop.ViewModels.Main;

/// <summary>
/// The part of the main window view model behind the windows that show one message.
/// </summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>
    /// The open message windows, keyed by message identifier.
    /// </summary>
    private readonly Dictionary<Guid, MessageWindowViewModel> _messageWindows = [];

    /// <summary>
    /// Shows a message in a window of its own; a message that has one already is brought to the front.
    /// </summary>
    /// <param name="item">The message.</param>
    public void OpenMessageWindow(MessageItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!_messageWindows.TryGetValue(item.Id, out var window))
        {
            window = new MessageWindowViewModel(this, item);
            _messageWindows[item.Id] = window;
        }

        using (BeginMessageScope(item))
        {
            _logger.LogInformation("Opening message {MessageId} in its own window", item.Id);
        }

        _dialogs.ShowMessage(window);
    }

    /// <summary>
    /// Forgets a message window that was closed.
    /// </summary>
    /// <param name="window">The window.</param>
    internal void ForgetMessageWindow(MessageWindowViewModel window)
    {
        _messageWindows.Remove(window.Item.Id);
    }

    /// <summary>
    /// Opens a compose window replying to a message.
    /// </summary>
    /// <param name="item">The message.</param>
    /// <returns>A task that completes when the window has been opened.</returns>
    internal Task ReplyToAsync(MessageItemViewModel item)
    {
        return _dialogs.ShowComposeAsync(_accounts, ComposeTemplates.Reply(item.Message, _localizer));
    }

    /// <summary>
    /// Opens a compose window forwarding a message.
    /// </summary>
    /// <param name="item">The message.</param>
    /// <returns>A task that completes when the window has been opened.</returns>
    internal Task ForwardMessageAsync(MessageItemViewModel item)
    {
        return _dialogs.ShowComposeAsync(_accounts, ComposeTemplates.Forward(item.Message, _localizer));
    }

    /// <summary>
    /// Determines whether a message can be moved to the archive folder of its account.
    /// </summary>
    /// <param name="item">The message.</param>
    /// <returns><see langword="true"/> if there is an archive folder the message is not in.</returns>
    internal bool CanArchiveMessage(MessageItemViewModel item)
    {
        return ArchiveFolderOf(item.Message.AccountId) is { } archive && archive.Id != item.Message.FolderId;
    }

    /// <summary>
    /// Deletes the message of a message window after the user confirmed.
    /// </summary>
    /// <param name="item">The message.</param>
    /// <returns><see langword="true"/> if the deletion was queued.</returns>
    internal async Task<bool> DeleteMessageAsync(MessageItemViewModel item)
    {
        if (!await ConfirmDeletionAsync([item]))
        {
            return false;
        }

        var permanent = WillDeletePermanently(item.Message);
        return await QueueRemovalAsync(
            item,
            "Error_DeleteMessage",
            "Status_DeleteQueued",
            async () =>
            {
                if (!permanent)
                {
                    return await ApplyToUnmovedAsync(item, QuickMailAction.Delete);
                }

                var key = new MessageKey(item.Message.AccountId, item.Id);
                return await _dispatcher.SendAsync(new DeleteMessagesCommand([key])) > 0;
            });
    }

    /// <summary>
    /// Archives the message of a message window.
    /// </summary>
    /// <param name="item">The message.</param>
    /// <returns><see langword="true"/> if the move was queued.</returns>
    internal Task<bool> ArchiveMessageAsync(MessageItemViewModel item)
    {
        return QueueRemovalAsync(
            item,
            "Error_ArchiveMessage",
            "Status_ArchiveQueued",
            () => ApplyToUnmovedAsync(item, QuickMailAction.Archive));
    }

    /// <summary>
    /// Moves a message only if it still is in the folder it was shown from. A window can stay open while the message is
    /// moved elsewhere, and deleting one that sits in Trash by now would remove it for good.
    /// </summary>
    /// <param name="item">The message.</param>
    /// <param name="action">What to do.</param>
    /// <returns><see langword="true"/> if the action was applied.</returns>
    private async Task<bool> ApplyToUnmovedAsync(MessageItemViewModel item, QuickMailAction action)
    {
        var result = await _dispatcher.SendAsync(
            new QuickMailActionCommand(item.Message.AccountId, item.Id, item.Message.FolderId, action));
        return result == QuickMailActionResult.Done;
    }

    /// <summary>
    /// Queues the removal of one message and takes it out of the list, leaving another selection alone.
    /// </summary>
    /// <param name="item">The message.</param>
    /// <param name="errorKey">The resource key describing the action if queuing fails.</param>
    /// <param name="queuedKey">The resource key of the status shown once queued; takes the number of messages.</param>
    /// <param name="queue">Queues the removal; returns <see langword="false"/> when the message is no longer there.</param>
    /// <returns><see langword="true"/> if the removal was queued.</returns>
    private async Task<bool> QueueRemovalAsync(MessageItemViewModel item, string errorKey, string queuedKey, Func<Task<bool>> queue)
    {
        bool? queued = null;
        await GuardedAsync(errorKey, async () => queued = await queue());
        if (queued != true)
        {
            if (queued == false)
            {
                StatusText = _localizer["Status_MessageGone"];
            }

            return false;
        }

        StatusText = _localizer.Format(queuedKey, 1);
        if (Messages.FirstOrDefault(m => m.Id == item.Id) is { } listed)
        {
            if (ReferenceEquals(listed, SelectedMessage) || SelectedMessages.Contains(listed))
            {
                RemoveFromList([listed]);
            }
            else
            {
                // A load that started before would bring the message back, so its result is discarded.
                _loadVersion++;
                Messages.Remove(listed);
            }
        }

        StartUnreadRefresh(item.Message.AccountId);
        await ReloadViewAsync();
        return true;
    }

    /// <summary>
    /// Gets the listed messages and those shown in message windows, each object once.
    /// </summary>
    /// <returns>The messages.</returns>
    private IEnumerable<MessageItemViewModel> ShownMessages()
    {
        return Messages.Union(_messageWindows.Values.Select(w => w.Item));
    }

    /// <summary>
    /// Brings the state shown in message windows up to date with freshly loaded messages.
    /// </summary>
    /// <param name="messages">The loaded messages.</param>
    private void UpdateMessageWindows(IReadOnlyList<MailMessage> messages)
    {
        if (_messageWindows.Count == 0)
        {
            return;
        }

        foreach (var message in messages)
        {
            if (_messageWindows.TryGetValue(message.Id, out var window))
            {
                window.Item.Update(message);
            }
        }
    }
}
