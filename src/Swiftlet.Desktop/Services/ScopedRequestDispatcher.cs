using System.Runtime.CompilerServices;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace Swiftlet.Desktop.Services;

/// <summary>
/// Dispatches each request through a fresh DI scope.
/// </summary>
/// <param name="scopeFactory">The service scope factory.</param>
public sealed class ScopedRequestDispatcher(IServiceScopeFactory scopeFactory) : IRequestDispatcher
{
    /// <inheritdoc />
    public async Task<TResponse> SendAsync<TResponse>(ICommand<TResponse> command, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        return await sender.Send(command, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<TResponse> SendAsync<TResponse>(IQuery<TResponse> query, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        return await sender.Send(query, cancellationToken);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TResponse> StreamAsync<TResponse>(
        IStreamQuery<TResponse> query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        await foreach (var item in sender.CreateStream(query, cancellationToken))
        {
            yield return item;
        }
    }
}
