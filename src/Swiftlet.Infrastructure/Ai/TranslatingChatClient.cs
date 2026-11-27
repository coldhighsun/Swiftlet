using System.ClientModel;
using System.Runtime.CompilerServices;
using Anthropic.Exceptions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Swiftlet.Application.Ai;

namespace Swiftlet.Infrastructure.Ai;

/// <summary>
/// Turns every failure of an SDK client into an <see cref="AiException"/>. SDK exceptions carry the service's answer,
/// which may echo mail content, so they must not travel further into code that logs them.
/// </summary>
/// <param name="inner">The SDK client.</param>
/// <param name="resource">What else has to be disposed with the client.</param>
/// <param name="logger">The logger; optional.</param>
internal sealed class TranslatingChatClient(IChatClient inner, IDisposable resource, ILogger? logger)
    : DelegatingChatClient(inner)
{
    /// <inheritdoc />
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.GetResponseAsync(messages, options, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Everything is translated, unknown failures included: any of them could quote the request.
            throw Translate(ex);
        }
    }

    /// <inheritdoc />
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IAsyncEnumerator<ChatResponseUpdate> updates;
        try
        {
            updates = base.GetStreamingResponseAsync(messages, options, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // See GetResponseAsync.
            throw Translate(ex);
        }

        await using (updates)
        {
            while (true)
            {
                try
                {
                    if (!await updates.MoveNextAsync())
                    {
                        break;
                    }
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // See GetResponseAsync.
                    throw Translate(ex);
                }

                yield return updates.Current;
            }
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            resource.Dispose();
        }
    }

    /// <summary>
    /// Builds the replacement of an SDK failure and records its type, which holds nothing of the request.
    /// </summary>
    /// <param name="exception">The failure.</param>
    /// <returns>The replacement.</returns>
    private AiException Translate(Exception exception)
    {
        var (failure, status) = exception switch
        {
            AnthropicApiException api => (FromStatus((int)api.StatusCode), (int?)api.StatusCode),
            ClientResultException { Status: > 0 } result => (FromStatus(result.Status), result.Status),
            ClientResultException or AnthropicIOException or HttpRequestException or IOException => (AiFailure.Network, null),
            AnthropicSseException => (AiFailure.Unavailable, null),

            // Not requested by the caller, so a time limit ran out.
            OperationCanceledException or TimeoutException => (AiFailure.Timeout, null),
            _ => (AiFailure.Unknown, null),
        };
        logger?.LogWarning(
            "The AI service request failed: {Failure}, status {Status}, {ExceptionType}",
            failure,
            status,
            exception.GetType().Name);
        return new AiException(failure, status);
    }

    /// <summary>
    /// Maps an HTTP status code to a failure.
    /// </summary>
    /// <param name="status">The status code.</param>
    /// <returns>The failure.</returns>
    private static AiFailure FromStatus(int status)
    {
        return status switch
        {
            401 or 403 => AiFailure.Unauthorized,
            404 => AiFailure.NotFound,
            408 => AiFailure.Timeout,
            402 or 429 => AiFailure.RateLimited,
            >= 500 => AiFailure.Unavailable,
            >= 400 => AiFailure.Rejected,

            // Redirects are not followed.
            >= 300 => AiFailure.NotFound,
            _ => AiFailure.Unknown,
        };
    }
}
