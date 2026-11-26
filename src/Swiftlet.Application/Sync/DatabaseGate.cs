namespace Swiftlet.Application.Sync;

/// <summary>
/// Lets parallel folder workers share one set of repositories: a database context is not thread safe, so database work runs one piece at a time.
/// </summary>
internal sealed class DatabaseGate : IDisposable
{
    /// <summary>
    /// The single slot.
    /// </summary>
    private readonly SemaphoreSlim _slot = new(1, 1);

    /// <summary>
    /// Runs database work exclusively.
    /// </summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="work">The work.</param>
    /// <param name="cancellationToken">Cancels the wait for the slot.</param>
    /// <returns>The result of the work.</returns>
    public async Task<T> RunAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken)
    {
        await _slot.WaitAsync(cancellationToken);
        try
        {
            return await work();
        }
        finally
        {
            _slot.Release();
        }
    }

    /// <summary>
    /// Runs database work without a result exclusively.
    /// </summary>
    /// <param name="work">The work.</param>
    /// <param name="cancellationToken">Cancels the wait for the slot.</param>
    /// <returns>A task that completes when the work is done.</returns>
    public Task RunAsync(Func<Task> work, CancellationToken cancellationToken)
    {
        return RunAsync(
            async () =>
            {
                await work();
                return true;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _slot.Dispose();
    }
}
