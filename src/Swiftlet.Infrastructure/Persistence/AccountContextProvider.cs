namespace Swiftlet.Infrastructure.Persistence;

/// <summary>
/// Hands out one <see cref="AccountDbContext"/> per account for the lifetime of a DI scope, so every repository of a
/// request shares the same tracked entities of an account, and disposes them with the scope.
/// </summary>
/// <param name="factory">The factory creating the account contexts.</param>
internal sealed class AccountContextProvider(IAccountDbContextFactory factory) : IAsyncDisposable
{
    /// <summary>
    /// The contexts opened so far in this scope.
    /// </summary>
    private readonly Dictionary<Guid, AccountDbContext> _contexts = [];

    /// <summary>
    /// Serializes access so concurrent repository calls cannot open two contexts for one account.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Gets the scope's context for an account, opening it on first use.
    /// </summary>
    /// <param name="accountId">The account identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The shared context; it is disposed with the scope.</returns>
    public async Task<AccountDbContext> GetAsync(Guid accountId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_contexts.TryGetValue(accountId, out var context))
            {
                context = await factory.CreateAsync(accountId, cancellationToken);
                _contexts[accountId] = context;
            }

            return context;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Saves the changes of every account context opened in this scope.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when all contexts are saved.</returns>
    public async Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        AccountDbContext[] open;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            open = [.. _contexts.Values];
        }
        finally
        {
            _gate.Release();
        }

        foreach (var context in open)
        {
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var context in _contexts.Values)
        {
            await context.DisposeAsync();
        }

        _contexts.Clear();
        _gate.Dispose();
    }
}
