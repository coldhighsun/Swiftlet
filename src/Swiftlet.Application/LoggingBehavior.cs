using System.Collections;
using System.Diagnostics;
using Mediator;
using Microsoft.Extensions.Logging;
using Swiftlet.Application.Logging;

namespace Swiftlet.Application;

/// <summary>
/// Logs every command and query with its duration and outcome, tagged with the mailbox and folder it concerns. Besides the
/// type name only what a request describes itself through <see cref="ILogDetails"/> is written, never the whole request,
/// because requests carry passwords, message bodies and recipients.
/// </summary>
/// <typeparam name="TMessage">The request type.</typeparam>
/// <typeparam name="TResponse">The response type.</typeparam>
/// <param name="logger">The logger.</param>
/// <param name="names">Resolves identifiers to names; optional.</param>
public sealed class LoggingBehavior<TMessage, TResponse>(
    ILogger<LoggingBehavior<TMessage, TResponse>> logger,
    LogNames? names = null)
    : IPipelineBehavior<TMessage, TResponse>
    where TMessage : IMessage
{
    /// <summary>
    /// The name directory; an empty one, which shows identifiers, when none is supplied.
    /// </summary>
    private readonly LogNames _names = names ?? new LogNames();

    /// <inheritdoc />
    public async ValueTask<TResponse> Handle(
        TMessage message,
        MessageHandlerDelegate<TMessage, TResponse> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(next);

        var level = message is IBaseCommand ? LogLevel.Information : LogLevel.Debug;

        // The scope surrounds the handler too, so everything it logs carries the same tags. A request about no single
        // account gets empty tags, so it never shows those of the work that happened to trigger it.
        using var scope = logger.BeginMailScope(
            message is IAccountRequest request ? _names.Account(request.AccountId) : null,
            message is IFolderRequest inFolder ? _names.Folder(inFolder.FolderId) : null);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var response = await next(message, cancellationToken);
            var result = response is ICollection items ? $" ({items.Count} items)" : string.Empty;
            logger.Log(level, "{Request} succeeded in {ElapsedMs} ms{Result}", Describe(message), stopwatch.ElapsedMilliseconds, result);
            return response;
        }
        catch (OperationCanceledException)
        {
            logger.Log(level, "{Request} was cancelled after {ElapsedMs} ms", Describe(message), stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            // Logged and rethrown: callers still decide how to present the failure.
            logger.LogError(ex, "{Request} failed after {ElapsedMs} ms", Describe(message), stopwatch.ElapsedMilliseconds);
            throw;
        }
    }

    /// <summary>
    /// Builds the text naming a request: its type name, followed by its details if it has any. It is built after the
    /// handler ran, so names the handler read are already known.
    /// </summary>
    /// <param name="message">The request.</param>
    /// <returns>The text.</returns>
    private string Describe(TMessage message)
    {
        var name = typeof(TMessage).Name;
        return message is ILogDetails details ? $"{name} ({details.DescribeForLog(_names)})" : name;
    }
}
