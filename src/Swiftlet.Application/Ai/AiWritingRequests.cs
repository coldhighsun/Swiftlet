using System.Runtime.CompilerServices;
using Mediator;
using Swiftlet.Application.Logging;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Application.Ai;

/// <summary>
/// How a text the user wrote is rewritten.
/// </summary>
public enum AiWritingAction
{
    /// <summary>
    /// Make it read well.
    /// </summary>
    Polish,

    /// <summary>
    /// Make it more formal.
    /// </summary>
    Formal,

    /// <summary>
    /// Make it shorter.
    /// </summary>
    Concise,

    /// <summary>
    /// Make it warmer.
    /// </summary>
    Friendly,

    /// <summary>
    /// Correct mistakes only.
    /// </summary>
    Proofread,

    /// <summary>
    /// Translate it into English.
    /// </summary>
    ToEnglish,

    /// <summary>
    /// Translate it into Simplified Chinese.
    /// </summary>
    ToChinese,
}

/// <summary>
/// Streams a rewritten version of a text the user wrote.
/// </summary>
/// <param name="Text">The user's own text, without quoted mail.</param>
/// <param name="Action">How to rewrite it.</param>
public sealed record RewriteTextQuery(string Text, AiWritingAction Action) : IStreamQuery<AiOutput>, ILogDetails
{
    /// <inheritdoc />
    public string DescribeForLog(LogNames names)
    {
        return $"action {Action}, {Text.Length} characters";
    }

    /// <summary>
    /// Describes the request without the text.
    /// </summary>
    /// <returns>The description.</returns>
    public override string ToString()
    {
        return DescribeForLog(new LogNames());
    }
}

/// <summary>
/// Handles <see cref="RewriteTextQuery"/>.
/// </summary>
/// <param name="answers">The connection to the service.</param>
public sealed class RewriteTextQueryHandler(AiAnswers answers) : IStreamQueryHandler<RewriteTextQuery, AiOutput>
{
    /// <inheritdoc />
    public IAsyncEnumerable<AiOutput> Handle(RewriteTextQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return StreamAsync(query, cancellationToken);
    }

    /// <summary>
    /// Asks the service. A text above the input limit is refused rather than cut, because the answer replaces it.
    /// </summary>
    /// <param name="query">The request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The parts of the answer.</returns>
    private async IAsyncEnumerable<AiOutput> StreamAsync(
        RewriteTextQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var target = answers.RequireTarget();
        ArgumentException.ThrowIfNullOrWhiteSpace(query.Text);
        if (query.Text.Length > target.Configuration.MaxInputCharacters)
        {
            throw new AiException(AiFailure.TooLong);
        }

        var draft = AiContentBuilder.Wrap("draft", query.Text);
        var instructions = AiPrompts.Rewrite(query.Action, draft.Tag);
        await foreach (var part in answers.AskAsync(target, instructions, draft.Text, cancellationToken))
        {
            yield return part;
        }
    }
}

/// <summary>
/// Streams a draft of a reply to a message.
/// </summary>
/// <param name="AccountId">The account that owns the message.</param>
/// <param name="MessageId">The message being replied to.</param>
/// <param name="Notes">What the user wants to say; may be empty.</param>
public sealed record DraftReplyQuery(Guid AccountId, Guid MessageId, string Notes)
    : IStreamQuery<AiOutput>, IAccountRequest, ILogDetails
{
    /// <inheritdoc />
    public string DescribeForLog(LogNames names)
    {
        return $"message {MessageId}, notes of {Notes.Length} characters";
    }

    /// <summary>
    /// Describes the request without the notes.
    /// </summary>
    /// <returns>The description.</returns>
    public override string ToString()
    {
        return DescribeForLog(new LogNames());
    }
}

/// <summary>
/// Handles <see cref="DraftReplyQuery"/>.
/// </summary>
/// <param name="answers">The connection to the service.</param>
/// <param name="messages">The message repository.</param>
public sealed class DraftReplyQueryHandler(AiAnswers answers, IMessageRepository messages)
    : IStreamQueryHandler<DraftReplyQuery, AiOutput>
{
    /// <inheritdoc />
    public IAsyncEnumerable<AiOutput> Handle(DraftReplyQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return StreamAsync(query, cancellationToken);
    }

    /// <summary>
    /// Asks the service.
    /// </summary>
    /// <param name="query">The request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The parts of the answer.</returns>
    private async IAsyncEnumerable<AiOutput> StreamAsync(
        DraftReplyQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var target = answers.RequireTarget();
        if (query.Notes.Length > target.Configuration.MaxInputCharacters)
        {
            throw new AiException(AiFailure.TooLong);
        }

        var message = await messages.GetAsync(query.AccountId, query.MessageId, cancellationToken)
            ?? throw new InvalidOperationException($"Message {query.MessageId} was not found.");
        var mail = AiContentBuilder.Build(message, target.Configuration.MaxInputCharacters);
        if (mail.IsTruncated)
        {
            yield return new AiOutput(AiOutputKind.InputTruncated);
        }

        var notes = AiContentBuilder.Wrap("notes", query.Notes.Trim());
        var instructions = AiPrompts.DraftReply(mail.Tag, notes.Tag);
        await foreach (var part in answers.AskAsync(
            target, instructions, $"{mail.Text}\n\n{notes.Text}", cancellationToken))
        {
            yield return part;
        }
    }
}
