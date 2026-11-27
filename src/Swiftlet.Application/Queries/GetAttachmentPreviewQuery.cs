using Mediator;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Logging;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Application.Queries;

/// <summary>
/// Builds a quick preview of an attachment, downloading it from the IMAP server when needed.
/// </summary>
/// <param name="AccountId">The account that owns the message.</param>
/// <param name="MessageId">The cached message identifier.</param>
/// <param name="AttachmentId">The attachment identifier.</param>
public sealed record GetAttachmentPreviewQuery(Guid AccountId, Guid MessageId, Guid AttachmentId)
    : IQuery<AttachmentPreview>, IAccountRequest, ILogDetails
{
    /// <inheritdoc />
    public string DescribeForLog(LogNames names)
    {
        return $"message {MessageId}, attachment {AttachmentId}";
    }
}

/// <summary>
/// Handles <see cref="GetAttachmentPreviewQuery"/>.
/// </summary>
/// <param name="messages">The message repository.</param>
/// <param name="folders">The folder repository.</param>
/// <param name="accounts">The account repository.</param>
/// <param name="imapFactory">The IMAP connection factory.</param>
/// <param name="previewer">Builds the preview from the content.</param>
/// <param name="cache">Keeps recent previews.</param>
public sealed class GetAttachmentPreviewQueryHandler(
    IMessageRepository messages,
    IFolderRepository folders,
    IAccountRepository accounts,
    IImapSessionFactory imapFactory,
    IAttachmentPreviewer previewer,
    AttachmentPreviewCache cache) : IQueryHandler<GetAttachmentPreviewQuery, AttachmentPreview>
{
    /// <summary>
    /// The largest attachment that is downloaded for a preview.
    /// </summary>
    public const long MaxDownloadBytes = 10 * 1024 * 1024;

    /// <inheritdoc />
    public async ValueTask<AttachmentPreview> Handle(GetAttachmentPreviewQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (cache.TryGet(query.AttachmentId, out var cached))
        {
            return cached;
        }

        var message = await messages.GetAsync(query.AccountId, query.MessageId, cancellationToken)
            ?? throw new InvalidOperationException($"Message {query.MessageId} was not found.");
        var attachment = message.Attachments.FirstOrDefault(a => a.Id == query.AttachmentId)
            ?? throw new InvalidOperationException($"Attachment {query.AttachmentId} was not found.");

        // The type and size are known before anything is downloaded, so most refusals cost no connection.
        if (!previewer.CanPreview(attachment.FileName, attachment.ContentType))
        {
            return AttachmentPreview.Unsupported;
        }

        if (attachment.Size > MaxDownloadBytes)
        {
            return AttachmentPreview.TooLarge;
        }

        var folder = await folders.GetAsync(message.AccountId, message.FolderId, cancellationToken)
            ?? throw new InvalidOperationException($"Folder {message.FolderId} was not found.");
        var account = await accounts.GetAsync(message.AccountId, cancellationToken)
            ?? throw new InvalidOperationException($"Account {message.AccountId} was not found.");

        await using var session = await imapFactory.ConnectAsync(account, cancellationToken);
        var content = await session.DownloadAttachmentAsync(folder.FullName, message.Uid, attachment.Ordinal, cancellationToken)
            ?? throw new InvalidOperationException("The attachment no longer exists on the server.");

        var preview = previewer.Create(attachment.FileName, attachment.ContentType, content);
        if (preview.Kind is not (AttachmentPreviewKind.Unsupported or AttachmentPreviewKind.TooLarge))
        {
            cache.Add(query.AttachmentId, preview);
        }

        return preview;
    }
}
