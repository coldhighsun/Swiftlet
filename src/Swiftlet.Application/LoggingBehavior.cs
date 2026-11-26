using System.Diagnostics;
using Mediator;
using Microsoft.Extensions.Logging;

namespace Swiftlet.Application;

/// <summary>
/// Logs every command and query with its duration and outcome. Only the request type name is written, never the request
/// contents, because requests carry passwords, message bodies and recipients.
/// </summary>
/// <typeparam name="TMessage">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
/// <param name="logger">The logger.</param>
public sealed class LoggingBehavior<TMessage, TResponse>(ILogger<LoggingBehavior<TMessage, TResponse>> logger)
    : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IMessage
{
    /// <inheritdoc />
    public async ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandlerDelegate<TMessage, TResponse> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        var name = typeof(TMessage).Name;
        var level = message is IBaseCommand ? LogLevel.Information : LogLevel.Debug;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await next(message, cancellationToken);
            logger.Log(level, "{Request} succeeded in {ElapsedMs} ms", name, stopwatch.ElapsedMilliseconds);
            return response;
        }
        catch (OperationCanceledException)
        {
            logger.Log(level, "{Request} was cancelled after {ElapsedMs} ms", name, stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            // Logged and rethrown: callers still decide how to present the failure.
            logger.LogError(ex, "{Request} failed after {ElapsedMs} ms", name, stopwatch.ElapsedMilliseconds);
            throw;
        }
    }
}
