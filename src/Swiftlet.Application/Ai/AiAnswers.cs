using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Swiftlet.Application.Ai;

/// <summary>
/// A service ready to be asked.
/// </summary>
/// <param name="Configuration">The settings in effect.</param>
/// <param name="Endpoint">The address.</param>
public sealed record AiTarget(AiConfiguration Configuration, Uri Endpoint);

/// <summary>
/// Sends one question to the AI service and streams the answer. Everything that leaves the computer for AI, apart from
/// the connection test, goes through here, and only to a <see cref="RequireTarget"/>.
/// </summary>
/// <param name="settings">The AI settings.</param>
/// <param name="clients">The client factory.</param>
/// <param name="keys">The API key store.</param>
public sealed class AiAnswers(IAiSettings settings, IAiClientFactory clients, AiKeyStore keys)
{
    /// <summary>
    /// The output limit for Claude, whose own default is far too small; its thinking counts too. Other services keep
    /// their own default, because many reject a limit above what the model supports.
    /// </summary>
    private const int ClaudeOutputTokens = 16_000;

    /// <summary>
    /// Gets the service to ask.
    /// </summary>
    /// <returns>The service.</returns>
    /// <exception cref="AiException">AI is switched off or not set up.</exception>
    public AiTarget RequireTarget()
    {
        var configuration = settings.Ai;
        return configuration.IsReady && AiEndpoint.TryParse(configuration.Endpoint, out var endpoint)
            ? new AiTarget(configuration, endpoint)
            : throw new AiException(AiFailure.NotConfigured);
    }

    /// <summary>
    /// Asks a question and streams the answer: its text without a leading reasoning block, then whether it was cut off.
    /// A refusal ends the answer with <see cref="AiOutputKind.Refused"/>.
    /// </summary>
    /// <param name="target">The service.</param>
    /// <param name="instructions">The instructions for the model.</param>
    /// <param name="content">The material the instructions are about.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The parts of the answer.</returns>
    public async IAsyncEnumerable<AiOutput> AskAsync(
        AiTarget target,
        string instructions,
        string content,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        var (configuration, endpoint) = target;
        var apiKey = await keys.GetAsync(configuration.Provider, endpoint, cancellationToken);
        using var client = clients.Create(
            new AiConnection(configuration.Provider, endpoint, configuration.Model, apiKey));
        List<ChatMessage> prompt = [new(ChatRole.System, instructions), new(ChatRole.User, content)];
        var options = new ChatOptions
        {
            MaxOutputTokens = configuration.Provider == AiProvider.Claude ? ClaudeOutputTokens : null,
        };

        var filter = new ThinkTagFilter();
        ChatFinishReason? finish = null;
        await foreach (var update in client.GetStreamingResponseAsync(prompt, options, cancellationToken))
        {
            finish = update.FinishReason ?? finish;
            var visible = filter.Push(update.Text);
            if (visible.Length > 0)
            {
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
            yield return new AiOutput(AiOutputKind.Text, rest);
        }

        if (finish == ChatFinishReason.Length)
        {
            yield return new AiOutput(AiOutputKind.OutputTruncated);
        }
    }
}
