using Mediator;
using Swiftlet.Application.Abstractions;
using Swiftlet.Application.Logging;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Application.Queries;

/// <summary>
/// Downloads the images embedded in a message body (referenced as <c>cid:</c> in its HTML) from the IMAP server.
/// </summary>
/// <param name="AccountId">The account that owns the message.</param>
/// <param name="MessageId">The cached message identifier.</param>
public sealed record GetInlineImagesQuery(Guid AccountId, Guid MessageId)
    : IQuery<IReadOnlyDictionary<string, InlineImage>>, IAccountRequest, ILogDetails
{
    /// <inheritdoc />
    public string DescribeForLog(LogNames names)
    {
        return $"message {MessageId}";
    }
}

/// <summary>
/// Handles <see cref="GetInlineImagesQuery"/>.
/// </summary>
/// <param name="messages">The message repository.</param>
/// <param name="folders">The folder repository.</param>
/// <param name="accounts">The account repository.</param>
/// <param name="imapFactory">The IMAP connection factory.</param>
public sealed class GetInlineImagesQueryHandler(
    IMessageRepository messages,
    IFolderRepository folders,
    IAccountRepository accounts,
    IImapSessionFactory imapFactory) : IQueryHandler<GetInlineImagesQuery, IReadOnlyDictionary<string, InlineImage>>
{
    /// <inheritdoc />
    public async ValueTask<IReadOnlyDictionary<string, InlineImage>> Handle(
        GetInlineImagesQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var message = await messages.GetAsync(query.AccountId, query.MessageId, cancellationToken)
            ?? throw new InvalidOperationException($"Message {query.MessageId} was not found.");

        // Most mail has no embedded images, so the server is only contacted when the HTML actually references some.
        var wanted = InlineImageReferences.Find(message.BodyHtml);
        if (wanted.Count == 0)
        {
            return new Dictionary<string, InlineImage>();
        }

        var folder = await folders.GetAsync(message.AccountId, message.FolderId, cancellationToken)
            ?? throw new InvalidOperationException($"Folder {message.FolderId} was not found.");
        var account = await accounts.GetAsync(message.AccountId, cancellationToken)
            ?? throw new InvalidOperationException($"Account {message.AccountId} was not found.");

        await using var session = await imapFactory.ConnectAsync(account, cancellationToken);
        return await session.DownloadInlineImagesAsync(folder.FullName, message.Uid, wanted, cancellationToken)
            ?? new Dictionary<string, InlineImage>();
    }
}
