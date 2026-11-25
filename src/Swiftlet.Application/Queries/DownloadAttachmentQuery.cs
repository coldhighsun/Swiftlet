using Mediator;
using Swiftlet.Application.Abstractions;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Application.Queries;

/// <summary>
/// The decoded content of a downloaded attachment.
/// </summary>
/// <param name="FileName">The file name.</param>
/// <param name="ContentType">The MIME content type.</param>
/// <param name="Content">The file content.</param>
public sealed record DownloadedAttachment(string FileName, string ContentType, ReadOnlyMemory<byte> Content);

/// <summary>
/// Downloads an attachment from the IMAP server on demand (only metadata is cached locally).
/// </summary>
/// <param name="MessageId">The cached message identifier.</param>
/// <param name="AttachmentId">The attachment identifier.</param>
public sealed record DownloadAttachmentQuery(Guid MessageId, Guid AttachmentId) : IQuery<DownloadedAttachment>;

/// <summary>
/// Handles <see cref="DownloadAttachmentQuery"/>.
/// </summary>
/// <param name="messages">The message repository.</param>
/// <param name="folders">The folder repository.</param>
/// <param name="accounts">The account repository.</param>
/// <param name="imapFactory">The IMAP connection factory.</param>
public sealed class DownloadAttachmentQueryHandler(
    IMessageRepository messages,
    IFolderRepository folders,
    IAccountRepository accounts,
    IImapSessionFactory imapFactory) : IQueryHandler<DownloadAttachmentQuery, DownloadedAttachment>
{
    /// <inheritdoc />
    public async ValueTask<DownloadedAttachment> Handle(DownloadAttachmentQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var message = await messages.GetAsync(query.MessageId, cancellationToken)
            ?? throw new InvalidOperationException($"Message {query.MessageId} was not found.");
        var attachment = message.Attachments.FirstOrDefault(a => a.Id == query.AttachmentId)
            ?? throw new InvalidOperationException($"Attachment {query.AttachmentId} was not found.");
        var folder = await folders.GetAsync(message.FolderId, cancellationToken)
            ?? throw new InvalidOperationException($"Folder {message.FolderId} was not found.");
        var account = await accounts.GetAsync(message.AccountId, cancellationToken)
            ?? throw new InvalidOperationException($"Account {message.AccountId} was not found.");

        await using var session = await imapFactory.ConnectAsync(account, cancellationToken);
        var content = await session.DownloadAttachmentAsync(folder.FullName, message.Uid, attachment.Ordinal, cancellationToken)
            ?? throw new InvalidOperationException("The attachment no longer exists on the server.");
        return new DownloadedAttachment(attachment.FileName, attachment.ContentType, content);
    }
}
