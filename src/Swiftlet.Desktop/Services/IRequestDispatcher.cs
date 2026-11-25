using Mediator;

namespace Swiftlet.Desktop.Services;

/// <summary>
/// Sends Mediator requests, each in its own DI scope, so long-lived view models never share a database context.
/// </summary>
public interface IRequestDispatcher
{
    /// <summary>
    /// Sends a command and returns its response.
    /// </summary>
    /// <typeparam name="TResponse">The response type.</typeparam>
    /// <param name="command">The command.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The response.</returns>
    Task<TResponse> SendAsync<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a query and returns its result.
    /// </summary>
    /// <typeparam name="TResponse">The result type.</typeparam>
    /// <param name="query">The query.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result.</returns>
    Task<TResponse> SendAsync<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default);
}
