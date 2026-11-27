using System.Diagnostics;
using System.Runtime.CompilerServices;
using Mediator;
using Microsoft.Extensions.Logging;
using Swiftlet.Application.Logging;

namespace Swiftlet.Application;

/// <summary>
/// Logs every streamed request with its duration and outcome, like <see cref="LoggingBehavior{TMessage,TResponse}"/>
/// does for the others. The streamed items are never written.
/// </summary>
/// <typeparam name="TMessage">The request type.</typeparam>
/// <typeparam name="TResponse">The item type.</typeparam>
/// <param name="logger">The logger.</param>
/// <param name="names">Resolves identifiers to names; optional.</param>
public sealed class StreamLoggingBehavior<TMessage, TResponse>(
    ILogger<StreamLoggingBehavior<TMessage, TResponse>> logger,
    LogNames? names = null)
    : IStreamPipelineBehavior<TMessage, TResponse>
    where TMessage : IStreamMessage
{
    /// <summary>
    /// The name directory; an empty one, which shows identifiers, when none is supplied.
    /// </summary>
    private readonly LogNames _names = names ?? new LogNames();

    /// <inheritdoc />
    public IAsyncEnumerable<TResponse> Handle(
        TMessage message,
        StreamHandlerDelegate<TMessage, TResponse> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);
        return LogAsync(message, next, cancellationToken);
    }

    /// <summary>
    /// Passes the items on and logs how the stream ended.
    /// </summary>
    /// <param name="message">The request.</param>
    /// <param name="next">The rest of the pipeline.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The items.</returns>
    private async IAsyncEnumerable<TResponse> LogAsync(
        TMessage message,
        StreamHandlerDelegate<TMessage, TResponse> next,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var request = message is ILogDetails details
            ? $"{typeof(TMessage).Name} ({details.DescribeForLog(_names)})"
            : typeof(TMessage).Name;
        var account = message is IAccountRequest owned ? _names.Account(owned.AccountId) : null;
        var stopwatch = Stopwatch.StartNew();
        var count = 0;
        var completed = false;
        var failed = false;
        await using var items = next(message, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                // A scope does not outlive a yield, so one surrounds each step of the handler.
                using (logger.BeginMailScope(account, null))
                {
                    try
                    {
                        if (!await items.MoveNextAsync())
                        {
                            break;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Logged and rethrown: callers still decide how to present the failure.
                        failed = true;
                        logger.LogError(ex, "{Request} failed after {ElapsedMs} ms", request, stopwatch.ElapsedMilliseconds);
                        throw;
                    }
                }

                count++;
                yield return items.Current;
            }

            completed = true;
        }
        finally
        {
            if (!failed)
            {
                using var scope = logger.BeginMailScope(account, null);
                logger.LogDebug(
                    "{Request} {Outcome} after {ElapsedMs} ms ({Count} items)",
                    request,
                    completed ? "succeeded" : "was stopped",
                    stopwatch.ElapsedMilliseconds,
                    count);
            }
        }
    }
}
