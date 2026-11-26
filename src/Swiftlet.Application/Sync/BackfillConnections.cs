using Microsoft.Extensions.Logging;
using Swiftlet.Application.Abstractions;

namespace Swiftlet.Application.Sync;

/// <summary>
/// The IMAP connections of one backfill pass: a primary connection owned by the caller and additional download connections
/// that are opened on first use and owned by this object. The additional connections are best effort: one that cannot be
/// opened, or that fails later, is simply left out.
/// </summary>
/// <param name="primary">The primary connection; it is not disposed by this object.</param>
/// <param name="maxHelpers">The largest number of additional connections.</param>
/// <param name="connect">Opens an additional connection; required when <paramref name="maxHelpers"/> is positive.</param>
/// <param name="logger">The logger; optional.</param>
public sealed class BackfillConnections(
    IImapSession primary,
    int maxHelpers = 0,
    Func<CancellationToken, Task<IImapSession>>? connect = null,
    ILogger? logger = null) : IAsyncDisposable
{
    /// <summary>
    /// The additional connections that are open and usable.
    /// </summary>
    private readonly List<IImapSession> _helpers = [];

    /// <summary>
    /// Whether the additional connections have been requested; they are opened only once per pass.
    /// </summary>
    private bool _opened;

    /// <summary>
    /// Gets the primary connection.
    /// </summary>
    public IImapSession Primary { get; } = primary;

    /// <summary>
    /// Gets the usable additional connections, opening them on the first call.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A snapshot of the additional connections.</returns>
    public async Task<IReadOnlyList<IImapSession>> GetHelpersAsync(CancellationToken cancellationToken = default)
    {
        if (!_opened && connect is not null)
        {
            _opened = true;
            var attempts = Enumerable.Range(0, maxHelpers).Select(_ => TryConnectAsync(connect, cancellationToken)).ToList();
            await Task.WhenAll(attempts);
        }

        lock (_helpers)
        {
            return [.. _helpers];
        }
    }

    /// <summary>
    /// Drops an additional connection that failed.
    /// </summary>
    /// <param name="helper">The connection.</param>
    /// <returns>A task that completes when the connection is released.</returns>
    public async ValueTask RetireAsync(IImapSession helper)
    {
        ArgumentNullException.ThrowIfNull(helper);
        bool removed;
        lock (_helpers)
        {
            removed = _helpers.Remove(helper);
        }

        if (removed)
        {
            await helper.DisposeAsync();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        List<IImapSession> open;
        lock (_helpers)
        {
            open = [.. _helpers];
            _helpers.Clear();
        }

        foreach (var helper in open)
        {
            await helper.DisposeAsync();
        }
    }

    /// <summary>
    /// Opens one additional connection; a failure is logged and leaves the pass with fewer connections.
    /// </summary>
    /// <param name="open">Opens the connection.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the attempt is over.</returns>
    private async Task TryConnectAsync(Func<CancellationToken, Task<IImapSession>> open, CancellationToken cancellationToken)
    {
        try
        {
            var session = await open(cancellationToken);
            lock (_helpers)
            {
                _helpers.Add(session);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Servers limit the connections per account; the pass works with the connections it has.
            logger?.LogWarning(ex, "An additional backfill connection could not be opened; continuing with fewer.");
        }
    }
}
