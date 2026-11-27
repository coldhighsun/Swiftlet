using Mediator;
using Swiftlet.Application.Logging;

namespace Swiftlet.Application.Ai;

/// <summary>
/// Streams a summary of a message.
/// </summary>
/// <param name="AccountId">The account that owns the message.</param>
/// <param name="MessageId">The message.</param>
/// <param name="Language">The language of the summary.</param>
/// <param name="Fresh">Whether to ask again although an answer is kept.</param>
public sealed record SummarizeMessageQuery(Guid AccountId, Guid MessageId, AiLanguage Language, bool Fresh = false)
    : IStreamQuery<AiOutput>, IAccountRequest, ILogDetails
{
    /// <inheritdoc />
    public string DescribeForLog(LogNames names)
    {
        return $"message {MessageId}, language {Language}, fresh {Fresh}";
    }
}

/// <summary>
/// Handles <see cref="SummarizeMessageQuery"/>.
/// </summary>
/// <param name="service">The service asking the model.</param>
public sealed class SummarizeMessageQueryHandler(AiMessageService service)
    : IStreamQueryHandler<SummarizeMessageQuery, AiOutput>
{
    /// <inheritdoc />
    public IAsyncEnumerable<AiOutput> Handle(SummarizeMessageQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return service.StreamAsync(
            query.AccountId, query.MessageId, AiMessageTask.Summarize, query.Language, query.Fresh, cancellationToken);
    }
}

/// <summary>
/// Streams a translation of a message.
/// </summary>
/// <param name="AccountId">The account that owns the message.</param>
/// <param name="MessageId">The message.</param>
/// <param name="Language">The target language.</param>
/// <param name="Fresh">Whether to ask again although an answer is kept.</param>
public sealed record TranslateMessageQuery(Guid AccountId, Guid MessageId, AiLanguage Language, bool Fresh = false)
    : IStreamQuery<AiOutput>, IAccountRequest, ILogDetails
{
    /// <inheritdoc />
    public string DescribeForLog(LogNames names)
    {
        return $"message {MessageId}, language {Language}, fresh {Fresh}";
    }
}

/// <summary>
/// Handles <see cref="TranslateMessageQuery"/>.
/// </summary>
/// <param name="service">The service asking the model.</param>
public sealed class TranslateMessageQueryHandler(AiMessageService service)
    : IStreamQueryHandler<TranslateMessageQuery, AiOutput>
{
    /// <inheritdoc />
    public IAsyncEnumerable<AiOutput> Handle(TranslateMessageQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return service.StreamAsync(
            query.AccountId, query.MessageId, AiMessageTask.Translate, query.Language, query.Fresh, cancellationToken);
    }
}
