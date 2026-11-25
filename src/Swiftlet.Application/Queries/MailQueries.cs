using Mediator;
using Swiftlet.Domain.Accounts;
using Swiftlet.Domain.Folders;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Application.Queries;

/// <summary>
/// Gets all configured accounts.
/// </summary>
public sealed record GetAccountsQuery : IQuery<IReadOnlyList<MailAccount>>;

/// <summary>
/// Handles <see cref="GetAccountsQuery"/>.
/// </summary>
/// <param name="accounts">The account repository.</param>
public sealed class GetAccountsQueryHandler(IAccountRepository accounts)
    : IQueryHandler<GetAccountsQuery, IReadOnlyList<MailAccount>>
{
    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<MailAccount>> Handle(GetAccountsQuery query, CancellationToken cancellationToken)
    {
        return await accounts.GetAllAsync(cancellationToken);
    }
}

/// <summary>
/// Gets the folders of an account.
/// </summary>
/// <param name="AccountId">The account identifier.</param>
public sealed record GetFoldersQuery(Guid AccountId) : IQuery<IReadOnlyList<MailFolder>>;

/// <summary>
/// Handles <see cref="GetFoldersQuery"/>.
/// </summary>
/// <param name="folders">The folder repository.</param>
public sealed class GetFoldersQueryHandler(IFolderRepository folders)
    : IQueryHandler<GetFoldersQuery, IReadOnlyList<MailFolder>>
{
    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<MailFolder>> Handle(GetFoldersQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await folders.GetByAccountAsync(query.AccountId, cancellationToken);
    }
}

/// <summary>
/// Gets a page of messages of a folder, newest first.
/// </summary>
/// <param name="FolderId">The folder identifier.</param>
/// <param name="Skip">The number of messages to skip.</param>
/// <param name="Take">The maximum number of messages to return.</param>
public sealed record GetMessagesQuery(Guid FolderId, int Skip, int Take) : IQuery<IReadOnlyList<MailMessage>>;

/// <summary>
/// Handles <see cref="GetMessagesQuery"/>.
/// </summary>
/// <param name="messages">The message repository.</param>
public sealed class GetMessagesQueryHandler(IMessageRepository messages)
    : IQueryHandler<GetMessagesQuery, IReadOnlyList<MailMessage>>
{
    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<MailMessage>> Handle(GetMessagesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await messages.GetByFolderAsync(query.FolderId, query.Skip, query.Take, cancellationToken);
    }
}

/// <summary>
/// Gets a page of the unified inbox (the Inbox folders of all accounts), newest first.
/// </summary>
/// <param name="Skip">The number of messages to skip.</param>
/// <param name="Take">The maximum number of messages to return.</param>
public sealed record GetUnifiedInboxQuery(int Skip, int Take) : IQuery<IReadOnlyList<MailMessage>>;

/// <summary>
/// Handles <see cref="GetUnifiedInboxQuery"/>.
/// </summary>
/// <param name="messages">The message repository.</param>
public sealed class GetUnifiedInboxQueryHandler(IMessageRepository messages)
    : IQueryHandler<GetUnifiedInboxQuery, IReadOnlyList<MailMessage>>
{
    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<MailMessage>> Handle(GetUnifiedInboxQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await messages.GetUnifiedInboxAsync(query.Skip, query.Take, cancellationToken);
    }
}

/// <summary>
/// Gets a page of the messages carrying a label, newest first.
/// </summary>
/// <param name="LabelId">The label identifier.</param>
/// <param name="Skip">The number of messages to skip.</param>
/// <param name="Take">The maximum number of messages to return.</param>
public sealed record GetMessagesByLabelQuery(Guid LabelId, int Skip, int Take) : IQuery<IReadOnlyList<MailMessage>>;

/// <summary>
/// Handles <see cref="GetMessagesByLabelQuery"/>.
/// </summary>
/// <param name="messages">The message repository.</param>
public sealed class GetMessagesByLabelQueryHandler(IMessageRepository messages)
    : IQueryHandler<GetMessagesByLabelQuery, IReadOnlyList<MailMessage>>
{
    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<MailMessage>> Handle(GetMessagesByLabelQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await messages.GetByLabelAsync(query.LabelId, query.Skip, query.Take, cancellationToken);
    }
}

/// <summary>
/// Searches cached messages.
/// </summary>
/// <param name="Text">The search text.</param>
/// <param name="AccountId">Restricts the search to one account, or <see langword="null"/> for all.</param>
/// <param name="Take">The maximum number of results.</param>
public sealed record SearchMessagesQuery(string Text, Guid? AccountId, int Take) : IQuery<IReadOnlyList<MailMessage>>;

/// <summary>
/// Handles <see cref="SearchMessagesQuery"/>.
/// </summary>
/// <param name="messages">The message repository.</param>
public sealed class SearchMessagesQueryHandler(IMessageRepository messages)
    : IQueryHandler<SearchMessagesQuery, IReadOnlyList<MailMessage>>
{
    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<MailMessage>> Handle(SearchMessagesQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await messages.SearchAsync(query.Text, query.AccountId, query.Take, cancellationToken);
    }
}
