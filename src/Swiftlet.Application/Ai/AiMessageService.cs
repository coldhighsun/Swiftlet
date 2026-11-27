using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.AI;
using Swiftlet.Domain.Messages;

namespace Swiftlet.Application.Ai;

/// <summary>
/// Asks the AI service about one message and streams the answer. This is the only place mail content leaves the
/// computer for AI, and only when the settings allow it.
/// </summary>
/// <param name="settings">The AI settings.</param>
/// <param name="messages">The message repository.</param>
/// <param name="clients">The client factory.</param>
/// <param name="keys">The API key store.</param>
/// <param name="cache">The recent answers.</param>
public sealed class AiMessageService(
    IAiSettings settings,
    IMessageRepository messages,
    IAiClientFactory clients,
    AiKeyStore keys,
    AiResultCache cache)
{
    /// <summary>
    /// The output limit for Claude, whose own default is far too small; its thinking counts too. Other services keep
    /// their own default, because many reject a limit above what the model supports.
    /// </summary>
    private const int ClaudeOutputTokens = 16_000;

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
        var configuration = settings.Ai;
        if (!configuration.IsReady || !AiEndpoint.TryParse(configuration.Endpoint, out var endpoint))
        {
            throw new AiException(AiFailure.NotConfigured);
        }

        var key = new AiResultKey(messageId, task, language, configuration);
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
        var content = AiContentBuilder.Build(message, configuration.MaxInputCharacters);
        var apiKey = await keys.GetAsync(configuration.Provider, endpoint, cancellationToken);
        using var client = clients.Create(
            new AiConnection(configuration.Provider, endpoint, configuration.Model, apiKey));
        if (content.IsTruncated)
        {
            yield return new AiOutput(AiOutputKind.InputTruncated);
        }

        List<ChatMessage> prompt =
        [
            new(ChatRole.System, AiPrompts.System(task, language, content.Tag)),
            new(ChatRole.User, content.Text),
        ];
        var options = new ChatOptions
        {
            MaxOutputTokens = configuration.Provider == AiProvider.Claude ? ClaudeOutputTokens : null,
        };

        var answer = new StringBuilder();
        var filter = new ThinkTagFilter();
        ChatFinishReason? finish = null;
        await foreach (var update in client.GetStreamingResponseAsync(prompt, options, cancellationToken))
        {
            finish = update.FinishReason ?? finish;
            var visible = filter.Push(update.Text);
            if (visible.Length > 0)
            {
                answer.Append(visible);
                yield return new AiOutput(AiOutputKind.Text, visible);
            }
        }

        if (finish == ChatFinishReason.ContentFilter)
        {
            yield return new AiOutput(AiOutputKind.Refused);
            yield break;
        }

        var rest = filter.Flush();
        if (rest.Length > 0)
        {
            answer.Append(rest);
            yield return new AiOutput(AiOutputKind.Text, rest);
        }

        var cut = finish == ChatFinishReason.Length;
        if (cut)
        {
            yield return new AiOutput(AiOutputKind.OutputTruncated);
        }

        if (!string.IsNullOrWhiteSpace(answer.ToString()))
        {
            cache.Set(key, new AiResult(answer.ToString(), content.IsTruncated, cut));
        }
    }
}
