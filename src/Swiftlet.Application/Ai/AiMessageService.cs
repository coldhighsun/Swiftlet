using System.Runtime.CompilerServices;
using System.Text;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Application.Ai;

/// <summary>
/// Asks the AI service about one message and keeps complete answers.
/// </summary>
/// <param name="answers">The connection to the service.</param>
/// <param name="messages">The message repository.</param>
/// <param name="cache">The recent answers.</param>
public sealed class AiMessageService(AiAnswers answers, IMessageRepository messages, AiResultCache cache)
{
    /// <summary>
    /// Streams the answer of a task about a message. A refused or interrupted answer is not kept.
    /// </summary>
    /// <param name="accountId">The account that owns the message.</param>
    /// <param name="messageId">The message.</param>
    /// <param name="task">The task.</param>
    /// <param name="language">The language of the answer.</param>
    /// <param name="fresh">Whether to ask again although an answer is kept.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The parts of the answer.</returns>
    public async IAsyncEnumerable<AiOutput> StreamAsync(
        Guid accountId,
        Guid messageId,
        AiMessageTask task,
        AiLanguage language,
        bool fresh,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var target = answers.RequireTarget();
        var key = new AiResultKey(messageId, task, language, target.Configuration);
        if (!fresh && cache.Get(key) is { } kept)
        {
            if (kept.IsInputTruncated)
            {
                yield return new AiOutput(AiOutputKind.InputTruncated);
            }

            yield return new AiOutput(AiOutputKind.Text, kept.Text);
            if (kept.IsOutputTruncated)
            {
                yield return new AiOutput(AiOutputKind.OutputTruncated);
            }

            yield break;
        }

        var message = await messages.GetAsync(accountId, messageId, cancellationToken)
            ?? throw new InvalidOperationException($"Message {messageId} was not found.");
        var content = AiContentBuilder.Build(message, target.Configuration.MaxInputCharacters);
        if (content.IsTruncated)
        {
            yield return new AiOutput(AiOutputKind.InputTruncated);
        }

        var answer = new StringBuilder();
        var refused = false;
        var cut = false;
        var instructions = AiPrompts.System(task, language, content.Tag);
        await foreach (var part in answers.AskAsync(target, instructions, content.Text, cancellationToken))
        {
            answer.Append(part.Text);
            refused |= part.Kind == AiOutputKind.Refused;
            cut |= part.Kind == AiOutputKind.OutputTruncated;
            yield return part;
        }

        if (!refused && !string.IsNullOrWhiteSpace(answer.ToString()))
        {
            cache.Set(key, new AiResult(answer.ToString(), content.IsTruncated, cut));
        }
    }
}
